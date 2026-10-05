from pathlib import Path
import hashlib, json, os, re

from .base import DetectionResult, RecoveryTechnique
from .generic_include_graph import recover_generic_include_graph
from ..pbo.core import (
    PBO_CPRS,
    pbo_entry_data,
    _pbo_decode_name,
    _pbo_decode_text,
    _pbo_has_substantive_script,
    _pbo_is_pure_comment_or_empty,
    _pbo_raw_name_suspicious,
    _pbo_safe_rel,
)

MIKERO_MAP_FILE = "PBO_MIKERO_RECOVERY_MAP.json"


def _properties(archive):
    out=[]
    for k,v in archive.get('properties',[]):
        out.append((_pbo_decode_name(k),_pbo_decode_name(v)))
    return out


def _sniff_type(data):
    if not data:
        return 'empty'
    if data.startswith(b'\x00raP'):
        return 'rapified'
    if data[:4] in (b'ODOL',b'MLOD',b'OD01'):
        return 'p3d'
    if data[:4]==b'OggS':
        return 'ogg'
    if data[:8]==b'RTM_0101':
        return 'rtm'
    if data[:4]==b'0DHT':
        return 'texheaders'
    if data[:4]==b'DDS ':
        return 'edds'
    # DayZ particle definitions and particle materials are text formats that
    # Mikero obfuscation commonly gives arbitrary binary-looking extensions.
    sample=data[:4096]
    if b'\0' not in sample:
        try:
            text=_pbo_decode_text(data)
        except Exception:
            text=''
        stripped=text.lstrip('\ufeff \t\r\n')
        if stripped.startswith('EffectDef'):
            return 'ptc'
        if stripped.startswith('ParticleSprite'):
            return 'emat'
        if stripped:
            printable=sum(1 for ch in stripped[:1024] if ch.isprintable() or ch in '\r\n\t')
            if printable/max(1,len(stripped[:1024]))>=0.90:
                return 'text'
    # Common BI PAA markers (same conservative set used by pbo-tool).
    if b'GGAT' in data[:16] or data[:2] in (b'\xff\x01',b'\xff\x05',b'\x44\x44',b'\x80\x80',b'\x15\x55',b'\x05\x05'):
        return 'paa'
    return 'unknown'


_TYPE_EXT={
    'p3d':'.p3d','ogg':'.ogg','rtm':'.rtm','edds':'.edds','ptc':'.ptc',
    'emat':'.emat','paa':'.paa','texheaders':'.bin','rapified':'.bin',
}

def _ogg_crc_table():
    table=[]
    for i in range(256):
        r=i<<24
        for _ in range(8):
            r=(((r<<1)^0x04C11DB7)&0xffffffff) if (r&0x80000000) else ((r<<1)&0xffffffff)
        table.append(r)
    return table

_OGG_CRC_TABLE=_ogg_crc_table()

def _ogg_crc(data):
    r=0
    for b in data:
        r=((r<<8)&0xffffffff)^_OGG_CRC_TABLE[((r>>24)&0xff)^b]
    return r

def _ogg_valid(data):
    """Validate every Ogg page CRC and boundary; no codec dependency needed."""
    if not data.startswith(b'OggS'):
        return False
    off=0; pages=0
    while off<len(data):
        if off+27>len(data) or data[off:off+4]!=b'OggS': return False
        segs=data[off+26]; header=27+segs
        if off+header>len(data): return False
        body=sum(data[off+27:off+header]); end=off+header+body
        if end>len(data): return False
        page=bytearray(data[off:end]); stored=int.from_bytes(page[22:26],'little')
        page[22:26]=b'\0\0\0\0'
        if _ogg_crc(page)!=stored: return False
        pages+=1; off=end
    return pages>0 and off==len(data)


def _extension_matches(name, sniffed):
    suffix=Path(name.replace('\\','/')).suffix.lower()
    expected=_TYPE_EXT.get(sniffed)
    if expected is None:
        return True
    if sniffed=='edds':
        return suffix in ('.edds','.dds')
    if sniffed=='texheaders':
        return suffix=='.bin'
    if sniffed=='rapified':
        # RaP can legitimately back config.bin or rvmat; with a mangled header
        # name we cannot infer authoring role from extension alone.
        return suffix in ('.bin','.rvmat')
    return suffix==expected


def _windows_safe_path(name):
    name=name.replace('/','\\').strip('\\')
    if not name:
        return False
    for part in name.split('\\'):
        if not part or part in ('.','..'):
            return False
        if part[-1:] in (' ','.'):
            return False
        for ch in part:
            if ord(ch)<32 or ch in '<>:"|?*':
                return False
    return True


def _mikero_evidence(archive):
    props=_properties(archive)
    mikero_vals=[v for k,v in props if k.lower()=='mikero']
    obf_vals=[v for k,v in props if k.lower()=='obfuscated']
    files=[]; suspicious=0; mismatches=0; wildcards=0
    for e in archive.get('entries',[]):
        if e.get('kind')!='file':
            continue
        raw=e.get('name',b'')
        name=_pbo_decode_name(raw).replace('/','\\')
        files.append((raw,name,e))
        if _pbo_raw_name_suspicious(raw):
            suspicious+=1
        if name=='*.*':
            wildcards+=1
        try:
            data=e.get('data') if 'data' in e else pbo_entry_data(archive,e)
            st=_sniff_type(data)
            if st not in ('unknown','empty','text') and not _extension_matches(name,st):
                mismatches+=1
        except Exception:
            pass
    branded=any('depbo.dll' in v.lower() for v in mikero_vals)
    obfuscated=bool(obf_vals)
    ratio=(suspicious/len(files)) if files else 0.0
    matched=branded and obfuscated and (wildcards>0 or (ratio>=0.20 and mismatches>=4))
    confidence=1.0 if matched and ratio>=0.20 and mismatches>=4 else (0.96 if matched else 0.0)
    evidence=(
        f'mikero_depbo={branded}',
        f'obfuscated_properties={len(obf_vals)}',
        f'wildcard_entries={wildcards}',
        f'suspicious_name_ratio={ratio:.3f}',
        f'content_extension_mismatches={mismatches}',
    )
    return matched,confidence,evidence


def _prepare_archive(archive):
    for e in archive.get('entries',[]):
        if e.get('kind')!='file':
            continue
        if 'decoded_name' not in e:
            e['decoded_name']=_pbo_decode_name(e.get('name',b'')).replace('/','\\')
        if 'data' not in e:
            try:
                e['data']=pbo_entry_data(archive,e)
            except Exception:
                e['data']=b''
    return archive


def _name_maps(archive):
    exact={}; stem={}
    for idx,e in enumerate(archive.get('entries',[])):
        if e.get('kind')!='file':
            continue
        n=e.get('decoded_name','').replace('/','\\').lstrip('\\')
        if not n:
            continue
        exact.setdefault(n.lower(),[]).append(idx)
        p=n.rsplit('\\',1)
        leaf=p[-1]
        if '.' in leaf:
            base=leaf.rsplit('.',1)[0]
            key=('\\'.join(p[:-1]+[base])).lower()
            stem.setdefault(key,[]).append(idx)
    return exact,stem


def _resolve_name(archive, exact, stem, prefix, current_name, inc):
    q=inc.replace('/','\\').lstrip('\\')
    if q.lower().startswith(prefix.lower()+'\\'):
        q=q[len(prefix)+1:]
    candidates=exact.get(q.lower(),[])
    if len(candidates)==1:
        return candidates[0]
    base=current_name.rsplit('\\',1)[0] if '\\' in current_name else ''
    rel=(base+'\\'+q if base else q)
    candidates=exact.get(rel.lower(),[])
    if len(candidates)==1:
        return candidates[0]
    return None


_INCLUDE_RE=re.compile(r'^\s*#include\s*[<"]([^">]+)[">]\s*$',re.I|re.M)


def _bridge_leaf(archive, exact, prefix, root_idx):
    """Return a unique substantive include leaf for a bridge root, if provable."""
    entries=archive['entries']
    visited=set(); leaves=[]; unresolved=[]; cycles=[]

    def walk(idx,stack):
        if idx in stack:
            cycles.append(idx); return
        e=entries[idx]
        try: text=_pbo_decode_text(e.get('data',b''))
        except Exception: return
        cur=e.get('decoded_name','')
        refs=list(_INCLUDE_RE.finditer(text))
        core=_INCLUDE_RE.sub('',text)
        if _pbo_has_substantive_script(core):
            leaves.append(idx)
        for m in refs:
            j=_resolve_name(archive,exact,{},prefix,cur,m.group(1))
            if j is None:
                unresolved.append((cur,m.group(1))); continue
            visited.add(j)
            walk(j,stack+[idx])

    walk(root_idx,[])
    uniq=[]
    for x in leaves:
        if x not in uniq: uniq.append(x)
    root_text=''
    try: root_text=_pbo_decode_text(entries[root_idx].get('data',b''))
    except Exception: pass
    root_core=_INCLUDE_RE.sub('',root_text)
    bridge=(not _pbo_has_substantive_script(root_core) and bool(_INCLUDE_RE.search(root_text)))
    if bridge and not unresolved and not cycles and len(uniq)==1:
        return uniq[0],visited,()
    return None,visited,tuple(unresolved)


def _class_scopes_for_offsets(text, offsets):
    offsets=sorted(set(offsets))
    result={}; target_pos=0; stack=[]; pending=None
    i=0; n=len(text); state='code'; quote=None
    while i<n:
        while target_pos<len(offsets) and offsets[target_pos]<=i:
            result[offsets[target_pos]]=tuple(stack)
            target_pos+=1
        ch=text[i]
        if state=='line':
            if ch=='\n': state='code'
            i+=1; continue
        if state=='block':
            if ch=='*' and i+1<n and text[i+1]=='/': state='code'; i+=2; continue
            i+=1; continue
        if state=='string':
            if ch=='\\': i+=2; continue
            if ch==quote: state='code'; quote=None
            i+=1; continue
        if ch=='/' and i+1<n and text[i+1]=='/': state='line'; i+=2; continue
        if ch=='/' and i+1<n and text[i+1]=='*': state='block'; i+=2; continue
        if ch in ('"',"'"): state='string'; quote=ch; i+=1; continue
        if ch.isalpha() or ch=='_':
            j=i+1
            while j<n and (text[j].isalnum() or text[j]=='_'): j+=1
            tok=text[i:j]
            if tok=='class':
                k=j
                while k<n and text[k].isspace(): k+=1
                m=k
                while m<n and (text[m].isalnum() or text[m]=='_'): m+=1
                if m>k: pending=text[k:m]
            i=j; continue
        if ch=='{':
            stack.append(pending or '')
            pending=None
        elif ch=='}':
            if stack: stack.pop()
            pending=None
        elif ch==';':
            pending=None
        i+=1
    while target_pos<len(offsets):
        result[offsets[target_pos]]=tuple(stack); target_pos+=1
    return result


def _safe_symbol(s):
    s=re.sub(r'[^A-Za-z0-9_]+','_',s or '').strip('_')
    return s or 'asset'


def _referenced_local_assets(config_text,prefix,archive,exact,stem):
    """Resolve PBO-local string references to actual header entries.

    Returns one record per unique source header entry.  The owner class is used
    only to generate a deterministic editable filename; the original reference
    is preserved in the proof map.
    """
    qre=re.compile(r'"([^"\r\n]+)"')
    matches=[]
    for m in qre.finditer(config_text):
        raw=m.group(1).replace('/','\\')
        # DayZ config references may use either addon-relative form
        #   Prefix\path\asset
        # or rooted virtual-P form
        #   \Prefix\path\asset
        # They address the same PBO-local payload.  Normalize only for lookup;
        # _rewrite_config preserves the author's rooted/non-rooted convention.
        lookup=raw.lstrip('\\')
        if not lookup.lower().startswith(prefix.lower()+'\\'):
            continue
        rel=lookup[len(prefix)+1:].lstrip('\\')
        if not rel or rel.lower().startswith('scripts\\'):
            continue
        ids=exact.get(rel.lower(),[])
        used_stem=False
        if not ids:
            ids=stem.get(rel.lower(),[]); used_stem=True
        if len(ids)!=1:
            continue
        idx=ids[0]; e=archive['entries'][idx]; data=e.get('data',b'')
        st=_sniff_type(data)
        if st in ('unknown','empty','text','rapified','texheaders'):
            continue
        matches.append(dict(start=m.start(1),end=m.end(1),full_ref=raw,rel=rel,index=idx,sniffed=st,used_stem=used_stem))
    scopes=_class_scopes_for_offsets(config_text,[x['start'] for x in matches])
    # Owner counters are deterministic and scoped by directory + class.
    counters={}; records=[]; by_idx={}
    used_names=set()
    for item in matches:
        idx=item['index']
        if idx in by_idx:
            by_idx[idx]['occurrences'].append((item['start'],item['end'],item['full_ref']))
            continue
        e=archive['entries'][idx]
        src_name=e.get('decoded_name','').replace('/','\\').lstrip('\\')
        st=item['sniffed']; ext=_TYPE_EXT.get(st,Path(src_name).suffix.lower() or '.bin')
        scope=[x for x in scopes.get(item['start'],()) if x]
        owner=_safe_symbol(scope[-1] if scope else Path(src_name).stem)
        ref_dir=item['rel'].rsplit('\\',1)[0] if '\\' in item['rel'] else ''
        key=(ref_dir.lower(),owner.lower(),ext)
        counters[key]=counters.get(key,0)+1
        ordinal=counters[key]
        # We do not yet know how many siblings the class has; numbering all
        # references makes the mapping stable if a later release adds one.
        base=f'{owner}_{ordinal:02d}'
        candidate=(ref_dir+'\\' if ref_dir else '')+base+ext
        seq=1
        while candidate.lower() in used_names:
            seq+=1; candidate=(ref_dir+'\\' if ref_dir else '')+f'{base}_{seq}'+ext
        used_names.add(candidate.lower())
        rec=dict(
            index=idx, source_header_name=src_name, source_raw_name_hex=e.get('name',b'').hex(),
            sniffed_type=st, recovered_path=candidate,
            payload_sha1=hashlib.sha1(e.get('data',b'')).hexdigest(), payload_size=len(e.get('data',b'')),
            owner_class=(scope[-1] if scope else ''), occurrences=[(item['start'],item['end'],item['full_ref'])],
            original_reference_had_extension=not item['used_stem'],
        )
        by_idx[idx]=rec; records.append(rec)
    return records


def _rewrite_config(config_text,prefix,asset_records):
    replacements=[]
    for rec in asset_records:
        safe_rel=rec['recovered_path'].replace('/','\\')
        safe_no_ext=safe_rel.rsplit('.',1)[0] if '.' in safe_rel.rsplit('\\',1)[-1] else safe_rel
        for start,end,old in rec['occurrences']:
            # Preserve the author's convention: sound sample references usually
            # omit .ogg; explicit-extension references remain explicit.
            leaf=old.rsplit('\\',1)[-1]
            had_ext='.' in leaf
            rooted='\\' if old.startswith('\\') else ''
            new=(rooted+prefix+'\\'+(safe_rel if had_ext else safe_no_ext))
            replacements.append((start,end,new,old))
    out=config_text
    for start,end,new,old in sorted(replacements,key=lambda x:x[0],reverse=True):
        if out[start:end]!=old:
            raise RuntimeError(f'Mikero config rewrite span drift: expected {old!r}, got {out[start:end]!r}')
        out=out[:start]+new+out[end:]
    return out,replacements


def _count_files(root):
    if not Path(root).exists(): return 0
    return sum(1 for p in Path(root).rglob('*') if p.is_file())


def recover_mikero_cyrillic(pbo_path,out_dir,archive=None,write_raw=False):
    pbo_path=Path(pbo_path); out_dir=Path(out_dir)
    archive=_prepare_archive(archive)
    # Reuse the generic include-graph engine for baseline safe payloads and
    # module/config discovery.  Technique-specific post-processing then proves
    # and restores Mikero's mangled references without changing T001 behavior.
    base=recover_generic_include_graph(pbo_path,out_dir,archive=archive,write_raw=write_raw)
    recovered_root=out_dir/'recovered_source'; recovered_root.mkdir(parents=True,exist_ok=True)
    props={k:v for k,v in _properties(archive)}
    prefix=props.get('prefix',pbo_path.stem)
    exact,stem=_name_maps(archive)

    bridges=[]
    # Canonical config.cpp and module-root .c/.cpp are authoring roots.  Under
    # this Mikero scheme their bodies are often moved to a fake-extension leaf.
    root_names=['config.cpp']
    report=(out_dir/'PBO_RECOVERY_REPORT.txt')
    report_text=report.read_text(encoding='utf-8',errors='replace') if report.exists() else ''
    mm=re.search(r'^Module modes:\s*(.+)$',report_text,re.M|re.I)
    module_dirs=[]
    if mm and mm.group(1).strip()!='-':
        module_dirs=[x.split('=',1)[0].strip() for x in mm.group(1).split(',') if x.strip()]
    for idx,e in enumerate(archive['entries']):
        if e.get('kind')!='file': continue
        n=e.get('decoded_name','').replace('/','\\')
        nl=n.lower()
        if not nl.endswith(('.c','.cpp')) or nl=='config.cpp': continue
        if any(nl.startswith(md.lower().rstrip('\\')+'\\') for md in module_dirs):
            root_names.append(n)

    for root_name in root_names:
        ids=exact.get(root_name.lower(),[])
        if len(ids)!=1: continue
        root_idx=ids[0]
        leaf_idx,visited,unresolved=_bridge_leaf(archive,exact,prefix,root_idx)
        if leaf_idx is None: continue
        leaf=archive['entries'][leaf_idx]
        leaf_data=leaf.get('data',b'')
        rel=_pbo_safe_rel(root_name)
        target=recovered_root/rel; target.parent.mkdir(parents=True,exist_ok=True)
        # config.cpp is rewritten below after asset mapping; scripts can be
        # restored byte-for-byte from their unique hidden source leaf now.
        if root_name.lower()!='config.cpp':
            target.write_bytes(leaf_data)
        bridges.append(dict(
            root_index=root_idx, root_name=root_name, leaf_index=leaf_idx,
            leaf_name=leaf.get('decoded_name',''), leaf_sha1=hashlib.sha1(leaf_data).hexdigest(),
            leaf_size=len(leaf_data), output_path=str(rel).replace('/','\\'), unresolved=list(unresolved),
        ))

    config_bridge=next((x for x in bridges if x['root_name'].lower()=='config.cpp'),None)
    config_original=''
    if config_bridge:
        config_original=_pbo_decode_text(archive['entries'][config_bridge['leaf_index']].get('data',b''))
    else:
        cfg=recovered_root/'config.cpp'
        if cfg.exists(): config_original=cfg.read_text(encoding='utf-8',errors='replace')

    asset_records=[]; rewrites=[]
    if config_original:
        asset_records=_referenced_local_assets(config_original,prefix,archive,exact,stem)
        # Only mangled/fake-extension references need renaming.  Safe canonical
        # paths already preserved by the baseline extractor are left untouched.
        needed=[]
        for rec in asset_records:
            e=archive['entries'][rec['index']]
            src=e.get('decoded_name','')
            if _pbo_raw_name_suspicious(e.get('name',b'')) or not _windows_safe_path(src) or not _extension_matches(src,rec['sniffed_type']):
                needed.append(rec)
        asset_records=needed
        rewritten,rewrites=_rewrite_config(config_original,prefix,asset_records)
        (recovered_root/'config.cpp').write_bytes(rewritten.encode('utf-8'))
        # Exact asset bytes are promoted under deterministic Windows-safe names.
        for rec in asset_records:
            target=recovered_root/_pbo_safe_rel(rec['recovered_path'])
            target.parent.mkdir(parents=True,exist_ok=True)
            target.write_bytes(archive['entries'][rec['index']].get('data',b''))

    # Prove every rewritten local reference resolves to its copied payload.
    mapping_errors=[]
    for rec in asset_records:
        target=recovered_root/_pbo_safe_rel(rec['recovered_path'])
        if not target.exists():
            mapping_errors.append(f"missing target {rec['recovered_path']}")
            continue
        if hashlib.sha1(target.read_bytes()).hexdigest()!=rec['payload_sha1']:
            mapping_errors.append(f"payload mismatch {rec['recovered_path']}")

    all_files=_count_files(recovered_root)
    specialized_indices=set(x['index'] for x in asset_records)
    specialized_indices.update(x['root_index'] for x in bridges)
    specialized_indices.update(x['leaf_index'] for x in bridges)
    # Safe baseline outputs are also trusted. Map by exact original header path
    # only; rewritten names are already covered above.
    for idx,e in enumerate(archive['entries']):
        if e.get('kind')!='file': continue
        n=e.get('decoded_name','')
        if not n or _pbo_raw_name_suspicious(e.get('name',b'')): continue
        try: rel=_pbo_safe_rel(n)
        except Exception: continue
        if (recovered_root/rel).exists(): specialized_indices.add(idx)

    # Mikero header obfuscation often plants payloads with convincing magic
    # but corrupted bodies. Ogg CRC is an independent integrity oracle: in the
    # Raid specimen every real Ogg validates and every injected Ogg decoy fails.
    valid_ogg=[]; invalid_ogg=[]; clean_ptc=[]; corrupt_ptc=[]
    mapped_by_index={int(x['index']):x for x in asset_records}
    for idx,e in enumerate(archive['entries']):
        if e.get('kind')!='file': continue
        data=e.get('data',b'')
        if data.startswith(b'OggS'):
            (valid_ogg if _ogg_valid(data) else invalid_ogg).append(idx)
        if data.startswith(b'EffectDef'):
            (clean_ptc if b'\0' not in data else corrupt_ptc).append(idx)

    def output_matches_index(idx):
        e=archive['entries'][idx]; data=e.get('data',b'')
        rec=mapped_by_index.get(idx)
        if rec:
            target=recovered_root/_pbo_safe_rel(rec['recovered_path'])
            return target.exists() and target.read_bytes()==data
        n=e.get('decoded_name','')
        if n and not _pbo_raw_name_suspicious(e.get('name',b'')):
            target=recovered_root/_pbo_safe_rel(n)
            return target.exists() and target.read_bytes()==data
        return False

    valid_ogg_preserved=sum(1 for idx in valid_ogg if output_matches_index(idx))
    clean_ptc_preserved=sum(1 for idx in clean_ptc if output_matches_index(idx))

    map_obj={
        'technique':'mikero-cyrillic-mangle',
        'prefix':prefix,
        'properties':_properties(archive),
        'bridge_roots':bridges,
        'rewritten_asset_references':asset_records,
        'rewrite_count':len(rewrites),
        'mapping_errors':mapping_errors,
        'valid_ogg_payloads':len(valid_ogg),
        'valid_ogg_preserved':valid_ogg_preserved,
        'invalid_ogg_decoys':len(invalid_ogg),
        'clean_ptc_payloads':len(clean_ptc),
        'clean_ptc_preserved':clean_ptc_preserved,
        'corrupt_ptc_decoys':len(corrupt_ptc),
        'trusted_indices':sorted(specialized_indices),
        'recovered_source_files':all_files,
    }
    (out_dir/MIKERO_MAP_FILE).write_text(json.dumps(map_obj,ensure_ascii=False,indent=2),encoding='utf-8')

    # Replace baseline counts with the real post-technique source count and add
    # technique-specific proof lines consumed by the independent verifier.
    lines=report_text.splitlines()
    def set_line(prefix_text,value):
        nonlocal lines
        for i,line in enumerate(lines):
            if line.lower().startswith(prefix_text.lower()):
                lines[i]=f'{prefix_text}{value}'; return
        lines.append(f'{prefix_text}{value}')
    set_line('Recovered source files: ',all_files)
    set_line('Reachable payload entries: ',len(specialized_indices))
    set_line('Filtered decoy payload files: ',len(invalid_ogg)+len(corrupt_ptc))
    # Rebuild the RECOVERED SOURCE list so Workbench diagnostics and humans see
    # the actual post-technique tree rather than T001's baseline pre-pass.
    try:
        rs=lines.index('RECOVERED SOURCE:')
        end=rs+1
        while end<len(lines) and lines[end].startswith('- '): end+=1
        final_files=sorted(str(x.relative_to(recovered_root)).replace('/','\\') for x in recovered_root.rglob('*') if x.is_file())
        lines=lines[:rs+1]+[f'- {x}' for x in final_files]+lines[end:]
    except ValueError:
        pass
    lines += [
        '', 'MIKERO CYRILLIC-MANGLE RECOVERY:',
        f'- Bridge roots with unique hidden source leaf: {len(bridges)}',
        f'- Mangled referenced assets renamed/recovered: {len(asset_records)}',
        f'- Rewritten config references: {len(rewrites)}',
        f'- Valid Ogg payloads preserved: {valid_ogg_preserved}/{len(valid_ogg)}',
        f'- CRC-invalid Ogg decoys excluded: {len(invalid_ogg)}',
        f'- Clean EffectDef/PTC payloads preserved: {clean_ptc_preserved}/{len(clean_ptc)}',
        f'- NUL-corrupted EffectDef decoys excluded: {len(corrupt_ptc)}',
        f'- Mapping errors: {len(mapping_errors)}',
    ]
    for b in bridges:
        lines.append(f"- SOURCE BRIDGE {b['root_name']} <= [{b['leaf_index']}] {b['leaf_name']}; sha1={b['leaf_sha1']}")
    for rec in asset_records:
        lines.append(f"- ASSET [{rec['index']}] {rec['source_header_name']} => {rec['recovered_path']}; {rec['sniffed_type']}; sha1={rec['payload_sha1']}")
    report.write_text('\n'.join(lines).rstrip()+'\n',encoding='utf-8')

    base.update(dict(
        recovered=all_files,
        reachable=max(base.get('reachable',0),len(specialized_indices)),
        mikero_bridges=len(bridges),
        mikero_assets=len(asset_records),
        mikero_rewrites=len(rewrites),
        mikero_mapping_errors=len(mapping_errors),
        mikero_valid_ogg=f'{valid_ogg_preserved}/{len(valid_ogg)}',
        mikero_invalid_ogg_decoys=len(invalid_ogg),
        mikero_clean_ptc=f'{clean_ptc_preserved}/{len(clean_ptc)}',
        mikero_corrupt_ptc_decoys=len(corrupt_ptc),
        mikero_map=out_dir/MIKERO_MAP_FILE,
    ))
    return base


class MikeroCyrillicMangleTechnique(RecoveryTechnique):
    technique_id='T005'
    technique_version='1.1.1'
    family='mikero-depbo'
    name='mikero-cyrillic-mangle'
    priority=120
    automatic_threshold=0.95

    def detect(self,archive):
        matched,confidence,evidence=_mikero_evidence(archive)
        return DetectionResult(self.name,matched,confidence,evidence)

    def recover(self,pbo_path,out_dir,archive,write_raw=False):
        return recover_mikero_cyrillic(pbo_path,out_dir,archive=archive,write_raw=write_raw)
