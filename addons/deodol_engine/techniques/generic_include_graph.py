from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from .base import DetectionResult, RecoveryTechnique
from ..version import SCRIPT_VERSION
from ..pbo.core import (PBO_CPRS, pbo_parse, pbo_entry_data, _pbo_decode_name, _pbo_decode_text,
    _pbo_is_pure_comment_or_empty, _pbo_has_substantive_script, _pbo_safe_rel,
    _pbo_raw_name_suspicious, _pbo_module_dirs_from_rap, _strip_mpg_guard, _collapse_blank_lines)

def recover_generic_include_graph(pbo_path,out_dir,archive=None,write_raw=False,module_output_map=None,
                                  suppress_empty_payloads=False):
    """Extract a PBO and reconstruct script roots through the real include graph.

    Output:
      recovered_source/  clean config.cpp + module scripts with internal packer
                         includes expanded in-place.
      PBO_RECOVERY_REPORT.txt
      PBO_MANIFEST.json   header/payload metadata; raw names retained as hex.
      raw_entries/        optional, only with write_raw=True.
    """
    pbo_path=Path(pbo_path); out_dir=Path(out_dir)
    out_dir.mkdir(parents=True,exist_ok=True)
    arc = archive if archive is not None else pbo_parse(pbo_path)
    entries=arc['entries']
    prop_dict={_pbo_decode_name(k):_pbo_decode_name(v) for k,v in arc['properties']}
    prefix=prop_dict.get('prefix',pbo_path.stem)
    module_output_map={str(k).replace('/', '\\').strip('\\').lower(): str(v).replace('/', '\\').strip('\\') for k,v in (module_output_map or {}).items()}

    name_map={}; payload_errors=[]; checksum_ok=0; methods={}
    text_entries=0; pure_decoys=0
    for idx,e in enumerate(entries):
        if e['kind']!='file': continue
        name=_pbo_decode_name(e['name']).replace('/','\\')
        e['decoded_name']=name
        name_map[name.lower()]=idx
        methods[e['method']]=methods.get(e['method'],0)+1
        try:
            data=pbo_entry_data(arc,e)
            e['data']=data
            if e['method']==PBO_CPRS: checksum_ok+=1
            # Only treat likely script/config data as text.  ASCII-ish content
            # with NUL bytes is not a script.
            sample=data[:1024]
            if data and b'\0' not in sample:
                text=_pbo_decode_text(data)
                printable=sum(1 for ch in text[:1024] if ch.isprintable() or ch in '\r\n\t')
                if not text[:1024] or printable/max(1,len(text[:1024]))>=0.85:
                    e['text']=text; text_entries+=1
                    if _pbo_is_pure_comment_or_empty(text): pure_decoys+=1
        except Exception as ex:
            payload_errors.append((idx,name,repr(ex)))

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

    def expand(idx,stack=None,visited=None):
        nonlocal cycle_count
        if stack is None: stack=[]
        if visited is None: visited=set()
        if idx in stack:
            cycle_count+=1
            return ''
        e=entries[idx]; text=e.get('text','')
        if _pbo_is_pure_comment_or_empty(text): return ''
        cur=e.get('decoded_name','')
        def repl(m):
            inc=m.group(1)
            j=resolve_include(cur,inc)
            if j is None:
                unresolved.add((cur,inc))
                return m.group(0)
            visited.add(j)
            sub=entries[j].get('text','')
            if _pbo_is_pure_comment_or_empty(sub):
                return ''
            return expand(j,stack+[idx],visited)
        return include_re.sub(repl,text)

    # Locate a usable textual config root.  A literal zero-byte config.cpp is
    # common in packed PBOs and must NOT suppress a real config.bin.
    config_idx=name_map.get('config.cpp')
    if config_idx is not None:
        t=entries[config_idx].get('text','')
        if not t.strip():
            config_idx=None
    if config_idx is None:
        for idx,e in enumerate(entries):
            t=e.get('text','')
            if 'CfgPatches' in t or 'CfgMods' in t:
                ex=expand(idx)
                if 'CfgPatches' in ex or 'CfgMods' in ex:
                    config_idx=idx; break

    recovered_dir=out_dir/'recovered_source'
    recovered_dir.mkdir(parents=True,exist_ok=True)
    recovered=[]; module_dirs=[]; reachable=set()
    config_text=''; rap_config_idx=None

    # Protected data-only PBO support. Preserve every payload with a sane
    # relative header path except source-code roots, which are reconstructed
    # separately through the include graph to avoid emitting decoy scripts.
    preserved_payloads=0; payload_name_collisions=[]; filtered_decoy_payloads=0
    filtered_empty_payloads=0
    for idx,e in enumerate(entries):
        if e.get('kind')!='file' or 'data' not in e: continue
        raw_name=e.get('name',b''); name=e.get('decoded_name','').replace('/','\\').lstrip('\\')
        if not name or _pbo_raw_name_suspicious(raw_name): continue
        suffix=Path(name).suffix.lower()
        if suffix in ('.c','.cpp'): continue
        # Some randomized Cprs protectors surround the real include graph with
        # thousands of empty entries and also plant a smaller set of empty
        # binary-looking files under Windows-safe names.  Empty payloads carry
        # no recoverable authoring data.  The specialized detector enables this
        # filter only after proving that archive-wide signature.
        if suppress_empty_payloads and not e.get('data',b''):
            filtered_empty_payloads+=1
            filtered_decoy_payloads+=1
            continue
        # Packer decoys frequently lie about their extension.  A real PAA/P3D/
        # OGG/etc. is binary, so an independently text-classified payload under
        # one of those extensions is an extension/content mismatch and must not
        # be promoted into recovered_source.  This also catches substantive
        # script fragments deliberately disguised as *.paa.
        binary_asset_exts={'.paa','.pac','.p3d','.ogg','.wav','.wss','.edds','.rtm','.anm','.bin'}
        if 'text' in e and suffix in binary_asset_exts:
            filtered_decoy_payloads+=1
            continue
        # Comment-only/empty textual junk is never a useful source asset even
        # when the chosen fake extension happens to be textual.
        if 'text' in e and _pbo_is_pure_comment_or_empty(e.get('text','')):
            filtered_decoy_payloads+=1
            continue
        rel=_pbo_safe_rel(name); target=recovered_dir/rel; target.parent.mkdir(parents=True,exist_ok=True)
        data=e.get('data',b'')
        if target.exists():
            try:
                if target.read_bytes()!=data: payload_name_collisions.append(str(rel).replace('/','\\'))
                continue
            except Exception: continue
        target.write_bytes(data); recovered.append(str(rel).replace('/','\\')); reachable.add(idx); preserved_payloads+=1
    if config_idx is not None:
        used=set(); config_text=expand(config_idx,visited=used); reachable.update(used); reachable.add(config_idx)
        config_text=_collapse_blank_lines(_strip_mpg_guard(config_text))
        for m in re.finditer(r'files\s*\[\s*\]\s*=\s*\{([^}]*)\}',config_text,re.I|re.S):
            for q in re.findall(r'"([^"]+)"',m.group(1)):
                q=q.replace('/','\\').lstrip('\\')
                if q.lower().startswith(prefix.lower()+'\\'):
                    q=q[len(prefix)+1:]
                module_dirs.append(q.rstrip('\\'))
        if module_output_map:
            def rewrite_module_path(match):
                original=match.group(1)
                q=original.replace('/','\\').lstrip('\\')
                had_prefix=q.lower().startswith(prefix.lower()+'\\')
                rel=q[len(prefix)+1:] if had_prefix else q
                mapped=module_output_map.get(rel.rstrip('\\').lower())
                if mapped is None:
                    return match.group(0)
                value=(prefix+'\\'+mapped if had_prefix else mapped).replace('\\','/')
                # DayZ accepts both separators; keep backslashes for conventional source.
                value=value.replace('/','\\')
                return '"'+value+'"'
            config_text=re.sub(r'"([^"]*)"',rewrite_module_path,config_text)
        (recovered_dir/'config.cpp').write_text(config_text,encoding='utf-8')
        recovered.append('config.cpp')

    # If config.cpp is absent/empty, inspect the rapified root config.bin for
    # CfgMods files[] path strings.  Preserve the binary config in recovered
    # source rather than manufacturing an incomplete config.cpp.
    root_bin=name_map.get('config.bin')
    if root_bin is not None:
        d=entries[root_bin].get('data',b'')
        if d.startswith(b'\x00raP'):
            rap_config_idx=root_bin
            module_dirs += _pbo_module_dirs_from_rap(d,prefix)
            if config_idx is None:
                target=recovered_dir/'config.bin'; target.write_bytes(d)
                if not any(x.lower()=='config.bin' for x in recovered): recovered.append('config.bin')
                reachable.add(root_bin)

    # Fall back to conventional DayZ script module roots if config recovery
    # was unavailable or does not enumerate them.
    if not module_dirs:
        seen=set()
        for e in entries:
            n=e.get('decoded_name','').replace('/','\\')
            m=re.match(r'(?i)(scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))(?:\\|$)',n)
            if m and m.group(1).lower() not in seen:
                module_dirs.append(m.group(1)); seen.add(m.group(1).lower())

    module_dirs=list(dict.fromkeys(x.lower() for x in module_dirs))
    script_roots=[]
    for idx,e in enumerate(entries):
        n=e.get('decoded_name','').replace('/','\\')
        nl=n.lower()
        if not nl.endswith(('.c','.cpp')): continue
        if not any(nl.startswith(md+'\\') or nl==md for md in module_dirs): continue
        used=set(); ex=expand(idx,visited=used)
        if not _pbo_has_substantive_script(ex): continue
        # Skip randomized nested files that are merely independent decoy code;
        # actual module root files are either direct children or are reachable
        # from another loaded root.  First collect all substantive candidates.
        script_roots.append((idx,n,ex,used))

    # Decide per module whether we are looking at a normal DayZ source tree or
    # a packer-obfuscated include graph.  In a normal CfgMods files[] directory,
    # the engine compiles .c files recursively, so nested files are roots too.
    # For an obfuscated tree, retain the stricter include-reachability strategy
    # so syntactically-valid decoy code is not dumped as real source.
    module_obfuscated={}
    for md in module_dirs:
        ids=[]; suspicious=0
        for idx,e in enumerate(entries):
            n=e.get('decoded_name','').replace('/','\\').lower()
            if n.startswith(md+'\\') and n.endswith(('.c','.cpp')):
                ids.append(idx)
                if _pbo_raw_name_suspicious(e.get('name',b'')):
                    suspicious+=1
        ratio=(suspicious/len(ids)) if ids else 0.0
        module_obfuscated[md]=(ratio>=0.10 or len(ids)>100)

    clean=[]; obf=[]
    for item in script_roots:
        idx,n,ex,used=item; nl=n.lower(); matched=None
        for md in module_dirs:
            if nl.startswith(md+'\\') or nl==md:
                matched=md; break
        if matched is not None and not module_obfuscated.get(matched,False):
            clean.append(item)
        else:
            obf.append(item)

    direct=[]; nested=[]
    for item in obf:
        idx,n,ex,used=item; nl=n.lower(); matched=None
        for md in module_dirs:
            if nl.startswith(md+'\\'):
                matched=md; break
        rel=n[len(matched)+1:] if matched else n
        if '\\' not in rel: direct.append(item)
        else: nested.append(item)
    loaded_ids={x[0] for x in direct}
    for _,_,_,used in direct: loaded_ids.update(used)
    final_roots=clean+direct+[x for x in nested if x[0] in loaded_ids]

    script_output_indices={}; script_path_collisions=[]
    for idx,n,ex,used in final_roots:
        reachable.add(idx); reachable.update(used)
        ex=_collapse_blank_lines(ex)
        nl=n.lower(); matched=None
        for md in module_dirs:
            if nl.startswith(md+'\\') or nl==md:
                matched=md; break
        mapped=module_output_map.get(matched) if matched is not None else None
        if mapped is not None:
            child=n[len(matched):].lstrip('\\')
            rel=Path(*mapped.split('\\'))/_pbo_safe_rel(child)
        else:
            rel=_pbo_safe_rel(n)
        base_rel=rel
        key=str(rel).replace('/','\\').lower()
        if key in script_output_indices or (recovered_dir/rel).exists():
            stem=rel.stem or 'script'
            suffix=rel.suffix or '.c'
            rel=rel.with_name(f'{stem}__entry_{idx:04d}{suffix}')
            serial=1
            while str(rel).replace('/','\\').lower() in script_output_indices or (recovered_dir/rel).exists():
                rel=rel.with_name(f'{stem}__entry_{idx:04d}_{serial}{suffix}'); serial+=1
            script_path_collisions.append((str(base_rel).replace('/','\\'),str(rel).replace('/','\\')))
        script_output_indices[str(rel).replace('/','\\').lower()]=idx
        target=recovered_dir/rel
        target.parent.mkdir(parents=True,exist_ok=True)
        target.write_text(ex,encoding='utf-8')
        recovered.append(str(rel).replace('/','\\'))

    # Optional forensic dump.  Filenames are index-prefixed so even mutually
    # colliding/illegal obfuscated names remain recoverable without data loss.
    if write_raw:
        rawdir=out_dir/'raw_entries'; rawdir.mkdir(parents=True,exist_ok=True)
        for idx,e in enumerate(entries):
            if e['kind']!='file' or 'data' not in e: continue
            rel=_pbo_safe_rel(e.get('decoded_name') or f'entry_{idx}')
            target=rawdir/(f'{idx:04d}_'+str(rel).replace('\\','_').replace('/','_'))
            target.write_bytes(e['data'])

    manifest=[]
    for idx,e in enumerate(entries):
        if e['kind']=='version':
            manifest.append(dict(index=idx,kind='version'))
            continue
        manifest.append(dict(
            index=idx, kind='file', raw_name_hex=e['name'].hex(),
            decoded_name=e.get('decoded_name',''), packing_method=e['method'],
            original_size=e['orig'], data_size=e['dsz'], data_offset=e['data_off'],
            decompressed_sha1=(hashlib.sha1(e['data']).hexdigest() if 'data' in e else None),
            reachable=(idx in reachable),
            pure_comment_decoy=(_pbo_is_pure_comment_or_empty(e.get('text','')) if 'text' in e else False),
        ))
    (out_dir/'PBO_MANIFEST.json').write_text(json.dumps({
        'source':str(pbo_path.resolve()), 'prefix':prefix,
        'archive_sha1_ok':arc['archive_sha_ok'], 'entries':manifest
    },ensure_ascii=False,indent=2),encoding='utf-8')

    report_lines=[
        'DayZ/Arma PBO script recovery report',
        f'Converter: {SCRIPT_VERSION}',
        f'Source: {pbo_path.resolve()}',
        f'Prefix: {prefix}',
        f'Header entries: {len(entries)}',
        f'Archive SHA1 trailer valid: {arc["archive_sha_ok"]}',
        f'Cprs blocks checksum-validated: {checksum_ok}',
        f'Text-like payloads: {text_entries}',
        f'Pure comment/empty decoys: {pure_decoys}',
        f'Module directories: {len(module_dirs)}',
        'Module modes: ' + (', '.join(f'{md}={"obfuscated" if module_obfuscated.get(md) else "recursive"}' for md in module_dirs) if module_dirs else '-'),
        f'Recovered source files: {len(recovered)}',
        f'Preserved safe payload files: {preserved_payloads}',
        f'Filtered decoy payload files: {filtered_decoy_payloads}',
        f'Filtered empty payload files: {filtered_empty_payloads}',
        f'Payload name collisions skipped: {len(payload_name_collisions)}',
        f'Script path collisions renamed: {len(script_path_collisions)}',
        f'Reachable payload entries: {len(reachable)}',
        f'Unresolved includes: {len(unresolved)}',
        f'Include cycles skipped: {cycle_count}',
        f'Payload errors: {len(payload_errors)}','',
        'RECOVERED SOURCE:'
    ]+[f'- {x}' for x in recovered]
    if unresolved:
        report_lines += ['', 'UNRESOLVED INCLUDES:']+[f'- {a} -> {b}' for a,b in sorted(unresolved)[:200]]
    if payload_name_collisions:
        report_lines += ['', 'PAYLOAD NAME COLLISIONS (first entry kept):']+[f'- {x}' for x in payload_name_collisions[:200]]
    if script_path_collisions:
        report_lines += ['', 'SCRIPT PATH COLLISIONS (renamed without loss):']+[f'- {a} => {b}' for a,b in script_path_collisions[:200]]
    if payload_errors:
        report_lines += ['', 'PAYLOAD ERRORS:']+[f'- [{i}] {n}: {er}' for i,n,er in payload_errors[:200]]
    report=out_dir/'PBO_RECOVERY_REPORT.txt'
    report.write_text('\n'.join(report_lines)+'\n',encoding='utf-8')
    return dict(entries=len(entries),recovered=len(recovered),reachable=len(reachable),
                cprs_valid=checksum_ok,errors=len(payload_errors),report=report,
                filtered_empty_payloads=filtered_empty_payloads)



class GenericIncludeGraphTechnique(RecoveryTechnique):
    technique_id = "T001"
    technique_version = "1.2.1"
    family = "generic"
    name = "generic-include-graph"
    priority = -100
    fallback = True

    def detect(self, archive):
        return DetectionResult(self.name, True, 0.10, ("safe fallback",))

    def recover(self, pbo_path, out_dir, archive, write_raw=False):
        return recover_generic_include_graph(pbo_path, out_dir, archive=archive, write_raw=write_raw)
