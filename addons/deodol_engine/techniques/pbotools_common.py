from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from ..version import SCRIPT_VERSION
from ..pbo.core import (PBO_CPRS, _pbo_decode_name, _pbo_decode_text, _pbo_has_substantive_script,
    _pbo_is_pure_comment_or_empty, _pbo_safe_rel, _collapse_blank_lines, pbo_entry_data)

PBO_TOOLS_MARKER_RECOVERY = True

PBO_TOOLS_V18_MARKER_RECOVERY = True

PBO_FULL_PAYLOAD_RECOVERY = True

_PBO_TOOLS_MARKER_RE=re.compile(r'(?i)^deobfuscated_file(\d+)(?:\.([A-Za-z0-9_]+))?$')

def _pbo_tools_marker_pairs(entries):
    """Recover numbered PBO Tools marker roots across known 1.8/1.9 layouts.

    PBO Tools 1.9.x observed layout:
        deobfuscated_fileN.ext -> target

    PBO Tools 1.8.1 observed layout:
        deobfuscated_fileN
        deobfuscated_fileN
        deobfuscated_fileN
        -> target

    The number of repeated extensionless control records is deliberately not
    hard-coded.  We collapse one consecutive run of the same numbered marker
    and trust only the first non-marker file immediately following that run.
    This keeps the rule archive-derived and avoids selecting thousands of
    syntactically valid decoys that share believable extensions/paths.
    """
    pairs=[]; idx=0
    while idx < len(entries)-1:
        e=entries[idx]
        if e.get('kind')!='file':
            idx+=1; continue
        name=e.get('decoded_name') or _pbo_decode_name(e.get('name',b''))
        name=name.replace('/','\\').strip('\\')
        m=_PBO_TOOLS_MARKER_RE.fullmatch(name)
        if not m:
            idx+=1; continue
        number=int(m.group(1)); explicit_ext=(m.group(2) or '').lower()
        marker_indices=[idx]; j=idx+1
        # 1.8.1 repeats the extensionless marker control record.  Collapse only
        # exact same-number/same-extension consecutive markers.
        while j < len(entries):
            e2=entries[j]
            if e2.get('kind')!='file': break
            n2=(e2.get('decoded_name') or _pbo_decode_name(e2.get('name',b''))).replace('/','\\').strip('\\')
            m2=_PBO_TOOLS_MARKER_RE.fullmatch(n2)
            if not m2 or int(m2.group(1))!=number or (m2.group(2) or '').lower()!=explicit_ext:
                break
            marker_indices.append(j); j+=1
        if j>=len(entries) or entries[j].get('kind')!='file':
            idx=j; continue
        nxt=entries[j]
        target=nxt.get('decoded_name') or _pbo_decode_name(nxt.get('name',b''))
        target=target.replace('/','\\')
        inferred_ext=Path(target).suffix.lower().lstrip('.')
        ext=explicit_ext or inferred_ext
        marker_name=name
        recovered_marker_name=marker_name if explicit_ext else marker_name+(('.'+ext) if ext else '')
        pairs.append(dict(marker_index=marker_indices[-1], marker_indices=marker_indices,
                          marker_repeat=len(marker_indices), number=number, ext=ext,
                          explicit_marker_ext=explicit_ext, marker_name=marker_name,
                          recovered_marker_name=recovered_marker_name,
                          target_index=j, target_name=target,
                          marker_style=('numbered-with-extension' if explicit_ext else 'numbered-extensionless-run')))
        idx=j+1
    pairs.sort(key=lambda x:x['number'])
    return pairs

def _pbo_tools_banner(arc):
    parts=[]
    for k,v in arc.get('properties',[]):
        try: parts.append(_pbo_decode_name(k)); parts.append(_pbo_decode_name(v))
        except Exception: pass
    return '\n'.join(parts)

def _pbo_tools_marker_scheme(arc,entries):
    pairs=_pbo_tools_marker_pairs(entries)
    if len(pairs)<3: return []
    nums=[x['number'] for x in pairs]
    # Strong structural signature: zero-based monotonically contiguous marker
    # sequence.  Banner is supporting evidence, not required, so future PBO
    # Tools builds that remove their product string remain recoverable.
    contiguous=(nums==list(range(nums[0],nums[0]+len(nums))) and nums[0]==0)
    banner=_pbo_tools_banner(arc).lower()
    branded=('pbo tools' in banner or 'pbo.tools' in banner)
    return pairs if contiguous or (branded and len(pairs)>=3) else []

def _pbo_tools_module_from_name(name):
    n=name.replace('/','\\')
    m=re.match(r'(?i)(scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))(?:\\|$)',n)
    return m.group(1).lower() if m else None

def _pbo_tools_target_safe_asset_path(pair):
    """Use original target path only for ordinary printable relative assets."""
    name=pair['target_name'].replace('/','\\').lstrip('\\')
    raw=name.encode('utf-8','surrogatepass')
    # Target paths for marker assets are normally intentionally left readable.
    # If a later PBO Tools build scrambles one, fall back to deterministic marker
    # naming instead of emitting Windows device/GUID/invisible paths.
    suspicious=any(ord(ch)<32 or ord(ch)>=127 or ch in '<>:"|?*' for ch in name)
    if suspicious or not name or name.startswith('\\') or '..' in name.split('\\'):
        return Path('_pbo_tools_recovered')/pair.get('recovered_marker_name',pair['marker_name'])
    return _pbo_safe_rel(name)

def _recover_pbo_tools_marked(pbo_path,out_dir,arc,write_raw=False):
    pbo_path=Path(pbo_path); out_dir=Path(out_dir); out_dir.mkdir(parents=True,exist_ok=True)
    entries=arc['entries']
    prop_dict={_pbo_decode_name(k):_pbo_decode_name(v) for k,v in arc['properties']}
    prefix=prop_dict.get('prefix',pbo_path.stem)

    name_map={}; payload_errors=[]; checksum_ok=0; methods={}; text_entries=0; pure_decoys=0
    for idx,e in enumerate(entries):
        if e.get('kind')!='file': continue
        name=_pbo_decode_name(e['name']).replace('/','\\'); e['decoded_name']=name
        # Keep first exact header name.  Obfuscators deliberately create
        # colliding aliases, so silently replacing with a later decoy is unsafe.
        name_map.setdefault(name.lower(),idx)
        methods[e['method']]=methods.get(e['method'],0)+1
        try:
            data=pbo_entry_data(arc,e); e['data']=data
            if e['method']==PBO_CPRS: checksum_ok+=1
            sample=data[:1024]
            if data and b'\0' not in sample:
                text=_pbo_decode_text(data)
                printable=sum(1 for ch in text[:1024] if ch.isprintable() or ch in '\r\n\t')
                if not text[:1024] or printable/max(1,len(text[:1024]))>=0.85:
                    e['text']=text; text_entries+=1
                    if _pbo_is_pure_comment_or_empty(text): pure_decoys+=1
        except Exception as ex:
            payload_errors.append((idx,name,repr(ex)))

    pairs=_pbo_tools_marker_scheme(arc,entries)
    if not pairs:
        raise ValueError('PBO Tools marker scheme was expected but not detected')

    include_re=re.compile(r'^\s*#include\s*[<"]([^">]+)[">]\s*$',re.I|re.M)
    unresolved=set(); cycle_count=0

    def resolve_include(cur_name,inc):
        s=inc.replace('/','\\').lstrip('\\')
        if s.lower().startswith(prefix.lower()+'\\'):
            s=s[len(prefix)+1:]
        j=name_map.get(s.lower())
        if j is not None: return j
        base=cur_name.rsplit('\\',1)[0] if '\\' in cur_name else ''
        rel=(base+'\\'+s if base else s).lower()
        return name_map.get(rel)

    def expand(idx,stack=None,visited=None,local_unresolved=None):
        nonlocal cycle_count
        if stack is None: stack=[]
        if visited is None: visited=set()
        if local_unresolved is None: local_unresolved=set()
        if idx in stack:
            cycle_count+=1; return ''
        e=entries[idx]; text=e.get('text',''); cur=e.get('decoded_name','')
        def repl(m):
            inc=m.group(1); j=resolve_include(cur,inc)
            if j is None:
                unresolved.add((cur,inc)); local_unresolved.add((cur,inc)); return m.group(0)
            visited.add(j)
            sub=entries[j].get('text','')
            if _pbo_is_pure_comment_or_empty(sub): return ''
            return expand(j,stack+[idx],visited,local_unresolved)
        return include_re.sub(repl,text)

    recovered_dir=out_dir/'recovered_source'; recovered_dir.mkdir(parents=True,exist_ok=True)
    recovered=[]; reachable=set(); trusted_targets=set(); marker_entries=set(); marker_script_roots=0; marker_assets=0
    marker_unresolved=[]; marker_details=[]
    module_dirs=[]

    for pair in pairs:
        mi=pair['marker_index']; ti=pair['target_index']; marker_entries.update(pair.get('marker_indices',[mi])); trusted_targets.add(ti)
        if ti>=len(entries) or entries[ti].get('kind')!='file':
            marker_details.append((pair['number'],'missing-target',pair['marker_name'],'')); continue
        e=entries[ti]; data=e.get('data',b''); target_name=e.get('decoded_name',pair['target_name'])
        ext=pair['ext'].lower()
        if ext in ('c','cpp') and _pbo_tools_module_from_name(target_name):
            used=set(); local=set(); expanded=expand(ti,visited=used,local_unresolved=local)
            if local:
                marker_unresolved.extend(sorted(local))
            if not _pbo_has_substantive_script(expanded):
                marker_details.append((pair['number'],'non-substantive-script',pair['marker_name'],target_name)); continue
            md=_pbo_tools_module_from_name(target_name)
            if md not in module_dirs: module_dirs.append(md)
            # The authoring filename has been intentionally destroyed by
            # ScramblePaths.  Preserve marker number/order and module instead of
            # pretending an inferred filename is source-exact.
            rel=Path(*md.split('\\'))/pair.get('recovered_marker_name',pair['marker_name'])
            target=recovered_dir/rel; target.parent.mkdir(parents=True,exist_ok=True)
            target.write_text(_collapse_blank_lines(expanded),encoding='utf-8')
            recovered.append(str(rel).replace('/','\\')); reachable.add(ti); reachable.update(used)
            marker_script_roots+=1
            marker_details.append((pair['number'],'script',pair['marker_name'],target_name))
        else:
            # Config and non-script marker roots are original payload bytes.
            rel=_pbo_tools_target_safe_asset_path(pair)
            target=recovered_dir/rel; target.parent.mkdir(parents=True,exist_ok=True); target.write_bytes(data)
            recovered.append(str(rel).replace('/','\\')); reachable.add(ti); marker_assets+=1
            marker_details.append((pair['number'],'asset',pair['marker_name'],target_name))
            if rel.name.lower()=='config.cpp':
                try:
                    cfg=_pbo_decode_text(data)
                    for m in re.finditer(r'files\s*\[\s*\]\s*=\s*\{([^}]*)\}',cfg,re.I|re.S):
                        for q in re.findall(r'"([^"]+)"',m.group(1)):
                            q=q.replace('/','\\').lstrip('\\')
                            if q.lower().startswith(prefix.lower()+'\\'): q=q[len(prefix)+1:]
                            q=q.rstrip('\\').lower()
                            if q and q not in module_dirs: module_dirs.append(q)
                except Exception: pass

    # Marker files themselves are control records and never source.
    # Include fragments reached by a trusted script root are semantically real,
    # but are folded into the expanded root and therefore are not emitted twice.

    if write_raw:
        rawdir=out_dir/'raw_entries'; rawdir.mkdir(parents=True,exist_ok=True)
        for idx,e in enumerate(entries):
            if e.get('kind')!='file' or 'data' not in e: continue
            rel=_pbo_safe_rel(e.get('decoded_name') or f'entry_{idx}')
            target=rawdir/(f'{idx:05d}_'+str(rel).replace('\\','_').replace('/','_'))
            target.write_bytes(e['data'])

    manifest=[]
    for idx,e in enumerate(entries):
        if e.get('kind')=='version': manifest.append(dict(index=idx,kind='version')); continue
        manifest.append(dict(index=idx,kind='file',raw_name_hex=e['name'].hex(),
            decoded_name=e.get('decoded_name',''),packing_method=e['method'],original_size=e['orig'],
            data_size=e['dsz'],data_offset=e['data_off'],
            decompressed_sha1=(hashlib.sha1(e['data']).hexdigest() if 'data' in e else None),
            reachable=(idx in reachable),trusted_marker_target=(idx in trusted_targets),
            pbo_tools_marker=(idx in marker_entries),
            pure_comment_decoy=(_pbo_is_pure_comment_or_empty(e.get('text','')) if 'text' in e else False)))
    (out_dir/'PBO_MANIFEST.json').write_text(json.dumps(dict(source=str(pbo_path.resolve()),prefix=prefix,
        archive_sha1_ok=arc['archive_sha_ok'],pbo_tools_marker_scheme=True,pbo_tools_marker_styles=sorted(set(x.get('marker_style','unknown') for x in pairs)),
        trusted_target_indices=sorted(trusted_targets),entries=manifest),ensure_ascii=False,indent=2),encoding='utf-8')

    decoys=max(0,sum(1 for e in entries if e.get('kind')=='file')-len(trusted_targets)-len(marker_entries))
    report_lines=[
        'DayZ/Arma PBO script recovery report',f'Converter: {SCRIPT_VERSION}',f'Source: {pbo_path.resolve()}',
        f'Prefix: {prefix}',f'Header entries: {len(entries)}',f'Archive SHA1 trailer valid: {arc["archive_sha_ok"]}',
        f'Cprs blocks checksum-validated: {checksum_ok}',f'Text-like payloads: {text_entries}',
        f'Pure comment/empty decoys: {pure_decoys}',f'PBO Tools marker scheme detected: 1',
        f'PBO Tools marker roots: {len(pairs)}',f'PBO Tools marker styles: '+(', '.join(sorted(set(x.get('marker_style','unknown') for x in pairs))) if pairs else '-'),f'PBO Tools marker repeat counts: '+(', '.join(str(x) for x in sorted(set(x.get('marker_repeat',1) for x in pairs))) if pairs else '-'),f'PBO Tools marker script roots: {marker_script_roots}',
        f'PBO Tools marker assets recovered: {marker_assets}',f'PBO Tools decoy entries ignored: {decoys}',
        f'Module directories: {len(module_dirs)}',
        'Module modes: '+(', '.join(f'{md}=pbo-tools-marker' for md in module_dirs) if module_dirs else '-'),
        f'Recovered source files: {len(recovered)}',f'Reachable payload entries: {len(reachable)}',
        f'Unresolved includes: {len(unresolved)}',f'PBO Tools marker include chains unresolved: {len(marker_unresolved)}',
        f'Include cycles skipped: {cycle_count}',f'Payload errors: {len(payload_errors)}','',
        'RECOVERED SOURCE:']+[f'- {x}' for x in recovered]
    report_lines += ['', 'PBO TOOLS MARKERS:']+[f'- {n}: {kind} {marker} -> {target}' for n,kind,marker,target in marker_details]
    if unresolved: report_lines += ['', 'UNRESOLVED INCLUDES:']+[f'- {a} -> {b}' for a,b in sorted(unresolved)[:200]]
    if payload_errors: report_lines += ['', 'PAYLOAD ERRORS:']+[f'- [{i}] {n}: {er}' for i,n,er in payload_errors[:200]]
    report=out_dir/'PBO_RECOVERY_REPORT.txt'; report.write_text('\n'.join(report_lines)+'\n',encoding='utf-8')
    return dict(entries=len(entries),recovered=len(recovered),reachable=len(reachable),cprs_valid=checksum_ok,
                errors=len(payload_errors),pbo_tools_markers=len(pairs),report=report)
