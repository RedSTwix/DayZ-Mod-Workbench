from pathlib import Path
import hashlib, json, re

from .base import DetectionResult, RecoveryTechnique
from ..version import SCRIPT_VERSION
from ..pbo.core import (
    PBO_CPRS,
    pbo_entry_data,
    _pbo_decode_name,
    _pbo_decode_text,
    _pbo_has_substantive_script,
    _pbo_is_pure_comment_or_empty,
    _pbo_safe_rel,
    _pbo_module_dirs_from_rap,
)

_JAPM_RE = re.compile(r'^__JAPM__\\unknown(\d+)\.txt$', re.I)
_DEOBF_RE = re.compile(r'^deobfuscated_file(\d+)\.c$', re.I)
_INCLUDE_RE = re.compile(r'^\s*#include\s*[<\"]([^\">]+)[\">]\s*$', re.I | re.M)
_TOPLEVEL_CLASS_RE = re.compile(r'^\s*(?:modded\s+)?class\s+([A-Za-z_][A-Za-z0-9_]*)\b', re.I | re.M)
_TOPLEVEL_FUNC_RE = re.compile(
    r'^[ \t]*(?:(?:static|proto|protected|private|override|final)\s+)*(?:ref\s+)?'
    r'[A-Za-z_][A-Za-z0-9_<>\[\].:]*[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]*\(',
    re.I | re.M,
)


def _properties_text(archive):
    parts=[]
    for k,v in archive.get('properties',[]):
        try: parts.append(_pbo_decode_name(k))
        except Exception: pass
        try: parts.append(_pbo_decode_name(v))
        except Exception: pass
    return '\n'.join(parts)


def _fire_marker_groups(archive):
    """Return structural Fire Packer groups.

    Fire Packer script PBOs observed here use:
      __JAPM__\\unknownN.txt
      deobfuscated_fileN.c
      <target payload>
      ... large decoy block ...

    We intentionally require both marker names and matching N.  The __JAPM__
    name alone is not a Fire Packer signature; JAPM itself uses it when it has
    to sanitize invalid filenames.
    """
    entries=archive.get('entries',[])
    groups=[]
    for i,e in enumerate(entries[:-2]):
        if e.get('kind')!='file':
            continue
        n=(e.get('decoded_name') or _pbo_decode_name(e.get('name',b''))).replace('/','\\')
        m=_JAPM_RE.match(n)
        if not m:
            continue
        number=int(m.group(1))
        e2=entries[i+1]
        e3=entries[i+2]
        if e2.get('kind')!='file' or e3.get('kind')!='file':
            continue
        n2=(e2.get('decoded_name') or _pbo_decode_name(e2.get('name',b''))).replace('/','\\')
        m2=_DEOBF_RE.match(n2)
        if not m2 or int(m2.group(1))!=number:
            continue
        n3=(e3.get('decoded_name') or _pbo_decode_name(e3.get('name',b''))).replace('/','\\')
        groups.append({
            'number': number,
            'japm_index': i,
            'marker_index': i+1,
            'target_index': i+2,
            'japm_name': n,
            'marker_name': n2,
            'target_name': n3,
        })
    groups.sort(key=lambda x:x['number'])
    return groups


def _module_from_target(name):
    n=name.replace('/','\\').lstrip('\\')
    m=re.match(r'(?i)(scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))(?:\\|$)',n)
    return m.group(1) if m else None


def _is_null_decoy(data):
    return not data or (len(data)<=4 and not data.strip(b'\x00'))



def _code_skeleton(text):
    """Mask comments/string literals while preserving offsets and newlines.

    This lets the semantic-name detector reason about brace depth without being
    confused by braces or function-looking text inside comments/strings.
    """
    src=text or ''
    out=list(src)
    i=0; state='code'; quote=''
    while i < len(src):
        c=src[i]; n=src[i+1] if i+1 < len(src) else ''
        if state=='code':
            if c=='/' and n=='/':
                out[i]=out[i+1]=' '; i+=2; state='line'; continue
            if c=='/' and n=='*':
                out[i]=out[i+1]=' '; i+=2; state='block'; continue
            if c in ('\"', "'"):
                quote=c; out[i]=' '; i+=1; state='string'; continue
            i+=1; continue
        if state=='line':
            if c=='\n': state='code'
            else: out[i]=' '
            i+=1; continue
        if state=='block':
            if c=='*' and n=='/':
                out[i]=out[i+1]=' '; i+=2; state='code'; continue
            if c!='\n': out[i]=' '
            i+=1; continue
        if state=='string':
            if c=='\\':
                out[i]=' '
                if i+1 < len(src):
                    if src[i+1]!='\n': out[i+1]=' '
                    i+=2
                else: i+=1
                continue
            if c==quote:
                out[i]=' '; i+=1; state='code'; continue
            if c!='\n': out[i]=' '
            i+=1
    return ''.join(out)


def _top_level_function_names(text):
    """Return function declarations that begin at brace depth zero.

    Fire Packer can erase the original filename from small utility files that
    contain only a global helper function.  We deliberately use a narrow rule:
    the source must have no class-derived filename and exactly one unique
    top-level function before we derive a filename from it.
    """
    code=_code_skeleton(text)
    depth_at=[0]*(len(code)+1)
    depth=0
    for i,c in enumerate(code):
        depth_at[i]=depth
        if c=='{': depth+=1
        elif c=='}' and depth>0: depth-=1
    names=[]
    for m in _TOPLEVEL_FUNC_RE.finditer(code):
        if depth_at[m.start()]!=0:
            continue
        name=m.group(1)
        if name.lower() in ('if','for','while','switch','catch'):
            continue
        if name not in names:
            names.append(name)
    return names


def _looks_like_dayz_script(text):
    """Narrow content check for a script hidden behind a non-script name.

    Do not classify generic text or textual RVMATs merely because they contain
    `class StageN`.  Require DayZ/Enforce-script evidence such as a modded or
    inherited class, a top-level function, or common executable keywords.
    """
    if not text or _pbo_is_pure_comment_or_empty(text):
        return False
    code=_code_skeleton(text)
    if re.search(r'^\s*modded\s+class\s+[A-Za-z_][A-Za-z0-9_]*\b',code,re.I|re.M):
        return True
    if re.search(r'^\s*class\s+[A-Za-z_][A-Za-z0-9_]*\s*(?::|extends\b)',code,re.I|re.M):
        return True
    if _top_level_function_names(text):
        return True
    return bool(re.search(r'\b(?:override|super\.|GetGame\s*\(|GetDayZGame\s*\(|EntityAI\b|RecipeBase\b)\b',code,re.I))


def _semantic_script_name(text, ext, used_names):
    """Infer a stable editable filename from preserved source semantics.

    Priority is deliberately conservative:
      1) primary top-level class (the established Fire Packer rule);
      2) when there is no class, exactly one top-level global function.

    The second rule fixes function-only utility files such as
    DefaultSelectionVisibility_EPIX without guessing when several global
    functions could have shared an arbitrary historical filename.  Collisions
    still force the caller to retain a neutral marker name.
    """
    classes=_TOPLEVEL_CLASS_RE.findall(text or '')
    if classes:
        primary=classes[0]
        evidence=tuple(classes)
    else:
        funcs=_top_level_function_names(text)
        if len(funcs)!=1:
            return None, tuple(funcs)
        primary=funcs[0]
        evidence=tuple(funcs)
    candidate=primary+ext
    key=candidate.lower()
    if key in used_names:
        return None, evidence
    return candidate, evidence

def _resolve_include_index(entries,name_map,prefix,cur_name,inc):
    s=inc.replace('/','\\').lstrip('\\')
    if s.lower().startswith(prefix.lower()+'\\'):
        s=s[len(prefix)+1:]
    j=name_map.get(s.lower())
    if j is not None:
        return j
    base=cur_name.rsplit('\\',1)[0] if '\\' in cur_name else ''
    rel=(base+'\\'+s if base else s).lower()
    return name_map.get(rel)


def _recover_fire_packer(pbo_path,out_dir,archive,write_raw=False):
    pbo_path=Path(pbo_path); out_dir=Path(out_dir); out_dir.mkdir(parents=True,exist_ok=True)
    entries=archive['entries']
    prop_dict={_pbo_decode_name(k):_pbo_decode_name(v) for k,v in archive.get('properties',[])}
    prefix=prop_dict.get('prefix',pbo_path.stem)

    checksum_ok=0; payload_errors=[]; text_entries=0; pure_decoys=0
    name_map={}
    for idx,e in enumerate(entries):
        if e.get('kind')!='file':
            continue
        name=(e.get('decoded_name') or _pbo_decode_name(e.get('name',b''))).replace('/','\\')
        e['decoded_name']=name
        # Keep first occurrence; Fire Packer intentionally emits duplicates.
        name_map.setdefault(name.lower(),idx)
        try:
            data=pbo_entry_data(archive,e); e['data']=data
            if e.get('method')==PBO_CPRS:
                checksum_ok+=1
            sample=data[:1024]
            if data and b'\x00' not in sample:
                text=_pbo_decode_text(data)
                printable=sum(1 for ch in text[:1024] if ch.isprintable() or ch in '\r\n\t')
                if printable/max(1,len(text[:1024]))>=0.85:
                    e['text']=text; text_entries+=1
                    if _pbo_is_pure_comment_or_empty(text): pure_decoys+=1
        except Exception as ex:
            payload_errors.append((idx,name,repr(ex)))

    groups=_fire_marker_groups(archive)
    marker_indices=set(); trusted_targets=set(); reachable=set()
    recovered=[]; details=[]; module_dirs=[]; unresolved=set(); cycles=0
    semantic_named=0; neutral_named=0; used_script_names={}
    recovered_dir=out_dir/'recovered_source'; recovered_dir.mkdir(parents=True,exist_ok=True)

    def expand(idx,stack=None,visited=None):
        """Expand Fire Packer include bridges and drop comment-only decoys.

        Some Fire Packer builds hide a real script payload behind a module root
        containing only #include directives.  Those included payloads may use
        fake extensions such as .rvmat, while sibling includes are pure-comment
        junk.  Expansion therefore happens before deciding whether a module root
        is substantive, and comment-only leaves contribute no source text.
        """
        nonlocal cycles
        stack=[] if stack is None else stack
        visited=set() if visited is None else visited
        if idx in stack:
            cycles+=1; return ''
        e=entries[idx]; text=e.get('text',''); cur=e.get('decoded_name','')
        if _pbo_is_pure_comment_or_empty(text):
            return ''
        def repl(m):
            inc=m.group(1)
            j=_resolve_include_index(entries,name_map,prefix,cur,inc)
            if j is None:
                unresolved.add((cur,inc)); return m.group(0)
            visited.add(j)
            sub=entries[j].get('text','')
            if _pbo_is_pure_comment_or_empty(sub):
                return ''
            return expand(j,stack+[idx],visited)
        return _INCLUDE_RE.sub(repl,text)

    # Pre-resolve every Fire Packer module root before writing any non-script
    # target.  This is intentionally two-phase: a marker target named Dump1.rvmat
    # can actually be a script fragment consumed later by an include-only
    # scripts\5_mission root.  Writing marker targets eagerly would leak that
    # fragment and any comment-only sibling into recovered_source.
    script_groups={}
    include_dependency_indices=set()
    substantive_include_indices=set()
    for g in groups:
        ti=g['target_index']; e=entries[ti]
        target_name=g['target_name']
        module=_module_from_target(target_name)
        if not module or not target_name.lower().endswith(('.c','.cpp')):
            continue
        include_indices=set()
        expanded=expand(ti,visited=include_indices)
        if not _pbo_has_substantive_script(expanded):
            continue
        # When the module root is pure include scaffolding and resolves to
        # exactly one substantive leaf, the editable script can preserve that
        # leaf byte-for-byte instead of adding whitespace left by include-line
        # substitution. This gives a stronger proof for Fire Packer bridges.
        exact_bridge_bytes=None
        root_without_includes=_INCLUDE_RE.sub('',e.get('text',''))
        substantive_leaves=[]
        if not _pbo_has_substantive_script(root_without_includes):
            for j in include_indices:
                sub=entries[j].get('text','')
                if _pbo_has_substantive_script(sub) and not _INCLUDE_RE.search(sub):
                    substantive_leaves.append(j)
            if len(substantive_leaves)==1:
                exact_bridge_bytes=entries[substantive_leaves[0]].get('data',b'')
                try: expanded=_pbo_decode_text(exact_bridge_bytes)
                except Exception: exact_bridge_bytes=None
        script_groups[g['number']]={
            'expanded': expanded,
            'include_indices': include_indices,
            'exact_bridge_bytes': exact_bridge_bytes,
        }
        include_dependency_indices.update(include_indices)
        for j in include_indices:
            sub=entries[j].get('text','')
            if sub and not _pbo_is_pure_comment_or_empty(sub):
                substantive_include_indices.add(j)

    include_fragments_consumed=0
    include_decoys_filtered=0
    include_roots_byte_exact=0

    for g in groups:
        marker_indices.update((g['japm_index'],g['marker_index']))
        ti=g['target_index']; e=entries[ti]
        data=e.get('data',b''); target_name=g['target_name']
        if _is_null_decoy(data):
            details.append((g['number'],'decoy-empty',g['marker_name'],target_name,len(data)))
            continue

        module=_module_from_target(target_name)
        if module and target_name.lower().endswith(('.c','.cpp')):
            info=script_groups.get(g['number'])
            if info is None:
                kind='decoy-expanded-empty' if _INCLUDE_RE.search(e.get('text','')) else 'decoy-nonscript'
                details.append((g['number'],kind,g['marker_name'],target_name,len(data)))
                continue
            text=e.get('text','')
            expanded=info['expanded']
            include_indices=info['include_indices']
            # Scrambled COM/LPT/AUX paths are not authoring filenames. Recover
            # the editable filename from the fully expanded source, because an
            # include-only bridge has no class/function declaration of its own.
            ext='.cpp' if target_name.lower().endswith('.cpp') else '.c'
            module_key=module.lower()
            name_used=used_script_names.setdefault(module_key,set())
            semantic_name,declared_symbols=_semantic_script_name(expanded,ext,name_used)
            if semantic_name:
                filename=semantic_name; semantic_named+=1
                name_kind='script-semantic-name'
            else:
                filename=f'deobfuscated_file{g["number"]}{ext}'; neutral_named+=1
                name_kind='script-neutral-name'
            name_used.add(filename.lower())
            rel=Path(*module.split('\\')) / filename
            target=recovered_dir/rel; target.parent.mkdir(parents=True,exist_ok=True)
            # Direct roots remain byte-exact. Include roots are reconstructed
            # from the preserved include payloads with comment-only junk removed.
            if not _INCLUDE_RE.search(text):
                target.write_bytes(data)
            elif info.get('exact_bridge_bytes') is not None:
                target.write_bytes(info['exact_bridge_bytes'])
                include_roots_byte_exact+=1
            else:
                target.write_bytes(expanded.encode('utf-8'))
            recovered.append(str(rel).replace('/','\\'))
            reachable.add(ti); trusted_targets.add(ti)
            for j in include_indices:
                sub=entries[j].get('text','')
                if sub and not _pbo_is_pure_comment_or_empty(sub):
                    reachable.add(j); trusted_targets.add(j)
            if module.lower() not in [x.lower() for x in module_dirs]: module_dirs.append(module)
            details.append((g['number'],name_kind,g['marker_name'],target_name,len(data),str(rel).replace('/','\\')))
            continue

        # A non-script marker target can be an internal include payload.  Such
        # fragments must never appear under their fake extension in source.
        if ti in include_dependency_indices:
            if ti in substantive_include_indices:
                include_fragments_consumed+=1
                trusted_targets.add(ti); reachable.add(ti)
                details.append((g['number'],'include-fragment-consumed',g['marker_name'],target_name,len(data)))
            else:
                include_decoys_filtered+=1
                details.append((g['number'],'decoy-include-fragment',g['marker_name'],target_name,len(data)))
            continue

        # Pure-comment textual marker targets are packer junk, regardless of
        # their apparent extension (Dump2.rvmat is a real-world example).
        text=e.get('text')
        if text is not None and _pbo_is_pure_comment_or_empty(text):
            details.append((g['number'],'decoy-comment',g['marker_name'],target_name,len(data)))
            continue

        # If a non-script target is actually a self-contained DayZ script but
        # is not referenced by a proven module include bridge, do not promote it
        # as an asset.  We lack module context to place it safely.
        if text is not None and _looks_like_dayz_script(text):
            candidate,_symbols=_semantic_script_name(text,'.c',set())
            if candidate is not None:
                details.append((g['number'],'orphan-script-fragment-skipped',g['marker_name'],target_name,len(data)))
                continue

        # Trusted non-script target. Preserve the original target path only if
        # it is a sane relative path. Marker relation + Fire banner are the
        # trust source; null/comment/include decoys were filtered above.
        rel=_pbo_safe_rel(target_name)
        target=recovered_dir/rel; target.parent.mkdir(parents=True,exist_ok=True)
        if target.exists():
            if target.read_bytes()==data:
                details.append((g['number'],'duplicate-identical',g['marker_name'],target_name,len(data)))
                trusted_targets.add(ti); reachable.add(ti)
                continue
            details.append((g['number'],'collision-skipped',g['marker_name'],target_name,len(data)))
            continue
        target.write_bytes(data)
        recovered.append(str(rel).replace('/','\\')); reachable.add(ti); trusted_targets.add(ti)
        if rel.name.lower()=='config.bin' and data.startswith(b'\x00raP'):
            for md in _pbo_module_dirs_from_rap(data,prefix):
                if md.lower() not in [x.lower() for x in module_dirs]: module_dirs.append(md)
        details.append((g['number'],'asset',g['marker_name'],target_name,len(data)))

    if write_raw:
        rawdir=out_dir/'raw_entries'; rawdir.mkdir(parents=True,exist_ok=True)
        for idx,e in enumerate(entries):
            if e.get('kind')!='file' or 'data' not in e: continue
            rel=_pbo_safe_rel(e.get('decoded_name') or f'entry_{idx}')
            (rawdir/(f'{idx:05d}_'+str(rel).replace('\\','_').replace('/','_'))).write_bytes(e['data'])

    manifest=[]
    for idx,e in enumerate(entries):
        if e.get('kind')=='version':
            manifest.append({'index':idx,'kind':'version'}); continue
        manifest.append({
            'index':idx,'kind':'file','raw_name_hex':e.get('name',b'').hex(),
            'decoded_name':e.get('decoded_name',''),'packing_method':e.get('method'),
            'original_size':e.get('orig'),'data_size':e.get('dsz'),'data_offset':e.get('data_off'),
            'decompressed_sha1':hashlib.sha1(e.get('data',b'')).hexdigest() if 'data' in e else None,
            'reachable':idx in reachable,'trusted_marker_target':idx in trusted_targets,
            'fire_packer_control':idx in marker_indices,
        })
    (out_dir/'PBO_MANIFEST.json').write_text(json.dumps({
        'source':str(pbo_path.resolve()),'prefix':prefix,'archive_sha1_ok':archive.get('archive_sha_ok'),
        'fire_packer_marker_scheme':True,'trusted_target_indices':sorted(trusted_targets),
        'entries':manifest,
    },ensure_ascii=False,indent=2),encoding='utf-8')

    files_total=sum(1 for e in entries if e.get('kind')=='file')
    decoys=max(0,files_total-len(trusted_targets)-len(marker_indices))
    report_lines=[
        'DayZ/Arma PBO script recovery report',f'Converter: {SCRIPT_VERSION}',f'Source: {pbo_path.resolve()}',
        f'Prefix: {prefix}',f'Header entries: {len(entries)}',f'Archive SHA1 trailer valid: {archive.get("archive_sha_ok")}',
        f'Cprs blocks checksum-validated: {checksum_ok}',f'Text-like payloads: {text_entries}',
        f'Pure comment/empty decoys: {pure_decoys}',
        'Fire Packer marker scheme detected: 1',f'Fire Packer marker groups: {len(groups)}',
        f'Fire Packer trusted targets: {len(trusted_targets)}',f'Fire Packer decoy entries ignored: {decoys}',
        f'Module directories: {len(module_dirs)}',
        'Module modes: '+(', '.join(f'{md}=fire-packer-marker' for md in module_dirs) if module_dirs else '-'),
        f'Recovered source files: {len(recovered)}','Preserved safe payload files: 0',
        f'Filtered decoy payload files: {sum(1 for d in details if d[1].startswith("decoy"))}',
        f'Fire Packer semantic script names: {semantic_named}',
        f'Fire Packer neutral script names: {neutral_named}',
        f'Fire Packer include fragments consumed: {include_fragments_consumed}',
        f'Fire Packer include decoys filtered: {include_decoys_filtered}',
        f'Fire Packer include roots byte-exact: {include_roots_byte_exact}',
        'Payload name collisions skipped: 0',f'Reachable payload entries: {len(reachable)}',
        f'Unresolved includes: {len(unresolved)}',f'Include cycles skipped: {cycles}',
        f'Payload errors: {len(payload_errors)}','', 'RECOVERED SOURCE:'
    ]+[f'- {x}' for x in recovered]
    report_lines += ['', 'FIRE PACKER MARKERS:']
    for item in details:
        n,kind,marker,target,size=item[:5]
        suffix=(f' => {item[5]}' if len(item)>5 else '')
        report_lines.append(f'- {n}: {kind} {marker} -> {target} ({size} bytes){suffix}')
    if unresolved:
        report_lines += ['', 'UNRESOLVED INCLUDES:']+[f'- {a} -> {b}' for a,b in sorted(unresolved)[:200]]
    if payload_errors:
        report_lines += ['', 'PAYLOAD ERRORS:']+[f'- [{i}] {n}: {er}' for i,n,er in payload_errors[:200]]
    report=out_dir/'PBO_RECOVERY_REPORT.txt'; report.write_text('\n'.join(report_lines)+'\n',encoding='utf-8')
    return {
        'entries':len(entries),'recovered':len(recovered),'reachable':len(reachable),
        'cprs_valid':checksum_ok,'errors':len(payload_errors),'fire_packer_markers':len(groups),'report':report,
    }


class FirePackerTechnique(RecoveryTechnique):
    technique_id='T004'
    technique_version='1.3.0'
    family='fire-packer'
    name='fire-packer-marker'
    priority=130
    automatic_threshold=0.95

    def detect(self,archive):
        banner=_properties_text(archive).lower()
        branded=('fire packer' in banner and 'pbo packer and obfuscator' in banner)
        if not branded:
            return DetectionResult(self.name,False,0.0,())
        groups=_fire_marker_groups(archive)
        if not groups:
            return DetectionResult(self.name,False,0.0,('fire_packer_banner=true','marker_groups=0'))
        nums=[g['number'] for g in groups]
        contiguous=(nums==list(range(nums[0],nums[0]+len(nums))) and nums[0]==0)
        spacings=[groups[i+1]['japm_index']-groups[i]['japm_index'] for i in range(len(groups)-1)]
        spacing_mode=max(set(spacings),key=spacings.count) if spacings else 0
        spacing_consistency=(spacings.count(spacing_mode)/len(spacings)) if spacings else 1.0
        # Fire banner is decisive; marker sequence and regular block spacing make
        # the specific recovery scheme independently auditable.
        confidence=1.0 if contiguous and len(groups)>=4 and spacing_consistency>=0.90 else 0.90
        evidence=(
            'fire_packer_banner=true',f'marker_groups={len(groups)}',
            f'sequence=0..{nums[-1]}',f'contiguous={contiguous}',
            f'block_spacing_mode={spacing_mode}',f'block_spacing_consistency={spacing_consistency:.3f}',
        )
        return DetectionResult(self.name,True,confidence,evidence)

    def recover(self,pbo_path,out_dir,archive,write_raw=False):
        return _recover_fire_packer(pbo_path,out_dir,archive,write_raw=write_raw)
