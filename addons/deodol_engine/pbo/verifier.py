from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from ..version import SCRIPT_VERSION
from .core import (PBO_CPRS, pbo_parse, pbo_entry_data, _pbo_decode_name, _pbo_decode_text,
    _pbo_has_substantive_script, _pbo_module_dirs_from_rap, _pbo_raw_name_suspicious, _pbo_safe_rel,
    _pbo_is_pure_comment_or_empty)
from ..techniques.pbotools_common import _pbo_tools_marker_scheme, _pbo_tools_module_from_name
from ..techniques.fire_packer import (_fire_marker_groups, _module_from_target as _fire_module_from_target,
    _semantic_script_name as _fire_semantic_script_name, _looks_like_dayz_script as _fire_looks_like_dayz_script)
from ..techniques.mikero_cyrillic import (_mikero_evidence, MIKERO_MAP_FILE, _rewrite_config, _ogg_valid)
from ..techniques.kgb_japm import (
    KGB_MAP_FILE, _apply_kgb_conditional_deobfuscation,
)
from ..techniques.generic_include_graph import recover_generic_include_graph
from ..techniques.randomized_cprs import RANDOMIZED_CPRS_MAP_FILE
from ..rvmat.recovery import verify_clean_rap_rvmat_text
from ..odol.parser import parse_odol, count
from ..odol.converter import write_mlod
from ..verifiers.p3d import verify_odol_to_mlod

def _pbo_verify_embedded_p3ds(arc,out_dir,trusted_indices=None,excluded_indices=None):
    """Decode and semantically round-trip every embedded ODOL53-55 P3D."""
    out_dir=Path(out_dir); temp_dir=out_dir/'_verify_p3d_tmp'
    results=[]
    trusted_indices=set(trusted_indices) if trusted_indices is not None else None
    excluded_indices=set(excluded_indices or ())
    for idx,e in enumerate(arc['entries']):
        if e.get('kind')!='file': continue
        if trusted_indices is not None and idx not in trusted_indices: continue
        if idx in excluded_indices: continue
        name=e.get('decoded_name') or _pbo_decode_name(e.get('name',b''))
        if not name.lower().endswith('.p3d'): continue
        try:
            data=e.get('data') if 'data' in e else pbo_entry_data(arc,e)
            if data[:4]==b'MLOD':
                results.append((name,'MLOD-ALREADY',0,'')); continue
            if data[:4]!=b'ODOL':
                results.append((name,'UNKNOWN-P3D',1,f'header={data[:4]!r}')); continue
            with tempfile.TemporaryDirectory(prefix='dayz_p3d_verify_') as td:
                inp=Path(td)/'in.p3d'; out=Path(td)/'out.p3d'; inp.write_bytes(data)
                model=parse_odol(inp)
                write_mlod(model,out)
                vr=verify_odol_to_mlod(model,out)
                results.append((name,vr['status'],len(vr['errors']),'; '.join(vr['errors'][:3])))
        except Exception as ex:
            results.append((name,'ERROR',1,repr(ex)))
    try:
        if temp_dir.exists(): shutil.rmtree(temp_dir)
    except Exception: pass
    return results

def _run_cfgconvert(exe,args,cwd=None):
    exe=Path(exe)
    if not exe.exists():
        raise FileNotFoundError(f'CfgConvert.exe not found: {exe}')
    cp=subprocess.run([str(exe)]+[str(x) for x in args],cwd=str(cwd) if cwd else None,
                      capture_output=True,text=True,errors='replace')
    if cp.returncode!=0:
        raise RuntimeError(f'CfgConvert failed ({cp.returncode}): {cp.stderr.strip() or cp.stdout.strip()}')
    return cp

def _normalize_xml_text(text):
    # CfgConvert XML is used only as a semantic comparison surface. Ignore
    # formatting/line-ending differences but keep element/attribute content.
    text=text.replace('\r\n','\n').replace('\r','\n')
    text=re.sub(r'>\s+<','><',text)
    return text.strip()

def verify_config_bin_with_cfgconvert(data,name,cfgconvert,recovery_dir):
    """Official-tool round trip: BIN -> CPP -> BIN, then XML semantic compare."""
    recovery_dir=Path(recovery_dir)
    with tempfile.TemporaryDirectory(prefix='dayz_cfg_verify_') as td:
        td=Path(td)
        src_bin=td/'original.bin'; cpp=td/'recovered.cpp'; round_bin=td/'roundtrip.bin'
        xml_a=td/'original.xml'; xml_b=td/'roundtrip.xml'
        src_bin.write_bytes(data)
        _run_cfgconvert(cfgconvert,['-txt','-dst',cpp,src_bin],td)
        if not cpp.exists() or cpp.stat().st_size==0:
            raise RuntimeError('CfgConvert produced no config.cpp')
        _run_cfgconvert(cfgconvert,['-bin','-dst',round_bin,cpp],td)
        if not round_bin.exists() or round_bin.stat().st_size==0:
            raise RuntimeError('CfgConvert produced no roundtrip config.bin')
        _run_cfgconvert(cfgconvert,['-xml','-dst',xml_a,src_bin],td)
        _run_cfgconvert(cfgconvert,['-xml','-dst',xml_b,round_bin],td)
        xa=_normalize_xml_text(xml_a.read_text(encoding='utf-8',errors='replace'))
        xb=_normalize_xml_text(xml_b.read_text(encoding='utf-8',errors='replace'))
        semantic=(xa==xb)
        byte_equal=(src_bin.read_bytes()==round_bin.read_bytes())
        # Persist the editable recovered config in a deterministic location.
        rel=_pbo_safe_rel(name)
        target=(recovery_dir/'recovered_source'/rel).with_suffix('.cpp')
        target.parent.mkdir(parents=True,exist_ok=True)
        # Avoid overwriting a textual config.cpp that was directly recovered.
        if target.exists():
            target=target.with_name(target.stem+'_from_bin.cpp')
        target.write_bytes(cpp.read_bytes())
        return dict(name=name,semantic=semantic,byte_equal=byte_equal,
                    recovered_cpp=str(target),cpp_size=target.stat().st_size,
                    original_sha256=hashlib.sha256(data).hexdigest(),
                    roundtrip_sha256=hashlib.sha256(round_bin.read_bytes()).hexdigest())

def _generic_source_snapshot(root):
    """Hash the source surface whose exact bytes come from graph expansion."""
    root=Path(root)
    result={}
    if not root.exists():
        return result
    for path in root.rglob('*'):
        if not path.is_file():
            continue
        rel=str(path.relative_to(root)).replace('/','\\')
        rel_lower=rel.lower()
        if (Path(rel).suffix.lower() not in ('.c','.cpp') and
                rel_lower not in ('config.cpp','config.bin')):
            continue
        data=path.read_bytes()
        result[rel_lower]=(hashlib.sha256(data).hexdigest(),len(data),rel)
    return result

def _compare_generic_source_replay(expected_root,actual_root):
    """Compare a fresh deterministic replay with the installed recovery tree."""
    expected=_generic_source_snapshot(expected_root)
    actual=_generic_source_snapshot(actual_root)
    errors=[]; verified=0
    for key,(digest,size,display) in expected.items():
        got=actual.get(key)
        if got is None:
            errors.append(f'missing replay output: {display}')
        elif got[0]!=digest or got[1]!=size:
            errors.append(
                f'replay content mismatch: {display} '
                f'(expected {size} bytes/{digest[:12]}, got {got[1]} bytes/{got[0][:12]})')
        else:
            verified+=1
    for key,(_digest,_size,display) in actual.items():
        if key not in expected:
            # CfgConvert verification intentionally adds editable companions
            # after recovery.  Permit only a .cpp whose matching config.bin is
            # part of the replay; every other extra source remains an error.
            candidate=key
            if candidate.endswith('_from_bin.cpp'):
                candidate=candidate[:-len('_from_bin.cpp')]+'.bin'
            elif candidate.endswith('.cpp'):
                candidate=candidate[:-len('.cpp')]+'.bin'
            if candidate in expected:
                continue
            errors.append(f'unexpected replay source: {display}')
    return verified,len(expected),errors

def verify_pbo_archive(pbo_path,recovery_dir,cfgconvert=None):
    """Independent post-recovery verification for PBO archives.

    BYTE-EXACT checks the original archive trailer and every Cprs payload checksum.
    SEMANTIC checks script-module coverage and embedded P3D ODOL->MLOD conversion.
    """
    pbo_path=Path(pbo_path); recovery_dir=Path(recovery_dir)
    arc=pbo_parse(pbo_path)
    cprs_total=0; cprs_ok=0; payload_errors=[]; files_total=0
    for idx,e in enumerate(arc['entries']):
        if e.get('kind')!='file': continue
        files_total+=1
        name=_pbo_decode_name(e.get('name',b'')); e['decoded_name']=name.replace('/','\\')
        try:
            data=pbo_entry_data(arc,e); e['data']=data
            if e['method']==PBO_CPRS:
                cprs_total+=1; cprs_ok+=1
        except Exception as ex:
            payload_errors.append((idx,name,repr(ex)))
    prop_dict={_pbo_decode_name(k):_pbo_decode_name(v) for k,v in arc['properties']}
    prefix=prop_dict.get('prefix',pbo_path.stem)
    pbo_tools_pairs=_pbo_tools_marker_scheme(arc,arc['entries'])
    pbo_tools_trusted={x['target_index'] for x in pbo_tools_pairs} if pbo_tools_pairs else set()
    fire_pairs=_fire_marker_groups(arc)
    fire_trusted=set()
    fire_script_roots={}
    fire_include_dependencies=set()
    fire_substantive_dependencies=set()
    if fire_pairs:
        # Rebuild the Fire Packer include graph independently from the recovery
        # output.  Include-only module roots are real scripts even when their
        # direct payload contains no class/function; fake-extension leaves can
        # carry the actual source and comment-only leaves are semantic no-ops.
        fire_name_map={}
        for idx,e in enumerate(arc['entries']):
            if e.get('kind')!='file': continue
            fire_name_map.setdefault(e.get('decoded_name','').lower(),idx)
        fire_include_re=re.compile(r'^\s*#include\s*[<"]([^">]+)[">]\s*$',re.I|re.M)

        def fire_resolve_include(cur_name,inc):
            q=inc.replace('/','\\').lstrip('\\')
            if q.lower().startswith(prefix.lower()+'\\'):
                q=q[len(prefix)+1:]
            j=fire_name_map.get(q.lower())
            if j is not None: return j
            base=cur_name.rsplit('\\',1)[0] if '\\' in cur_name else ''
            rel=(base+'\\'+q if base else q).lower()
            return fire_name_map.get(rel)

        def fire_expand(idx,stack=None,visited=None):
            stack=[] if stack is None else stack
            visited=set() if visited is None else visited
            if idx in stack: return ''
            e=arc['entries'][idx]
            try: text=_pbo_decode_text(e.get('data',b''))
            except Exception: return ''
            if _pbo_is_pure_comment_or_empty(text): return ''
            cur=e.get('decoded_name','')
            def repl(m):
                j=fire_resolve_include(cur,m.group(1))
                if j is None: return m.group(0)
                visited.add(j)
                try: sub=_pbo_decode_text(arc['entries'][j].get('data',b''))
                except Exception: return ''
                if _pbo_is_pure_comment_or_empty(sub): return ''
                return fire_expand(j,stack+[idx],visited)
            return fire_include_re.sub(repl,text)

        # Pass 1: prove loaded module script roots after include expansion.
        for pair in fire_pairs:
            ti=pair['target_index']
            if ti>=len(arc['entries']): continue
            e=arc['entries'][ti]
            name=e.get('decoded_name','')
            if not (_fire_module_from_target(name) and name.lower().endswith(('.c','.cpp'))):
                continue
            visited=set(); expanded=fire_expand(ti,visited=visited)
            if not _pbo_has_substantive_script(expanded):
                continue
            expected_bytes=None
            try: root_text=_pbo_decode_text(e.get('data',b''))
            except Exception: root_text=''
            root_without_includes=fire_include_re.sub('',root_text)
            if not _pbo_has_substantive_script(root_without_includes):
                substantive_leaves=[]
                for j in visited:
                    try: sub=_pbo_decode_text(arc['entries'][j].get('data',b''))
                    except Exception: continue
                    if _pbo_has_substantive_script(sub) and not fire_include_re.search(sub):
                        substantive_leaves.append(j)
                if len(substantive_leaves)==1:
                    expected_bytes=arc['entries'][substantive_leaves[0]].get('data',b'')
                    try: expanded=_pbo_decode_text(expected_bytes)
                    except Exception: expected_bytes=None
            fire_script_roots[ti]={
                'expanded': expanded,
                'visited': visited,
                'expected_bytes': expected_bytes,
            }
            fire_trusted.add(ti)
            fire_include_dependencies.update(visited)

        # Included source fragments are trusted inputs only when they contain
        # non-comment text. Pure-comment siblings remain decoys.
        for j in fire_include_dependencies:
            if j>=len(arc['entries']): continue
            try: sub=_pbo_decode_text(arc['entries'][j].get('data',b''))
            except Exception: continue
            if sub and not _pbo_is_pure_comment_or_empty(sub):
                fire_substantive_dependencies.add(j)
                fire_trusted.add(j)

        # Pass 2: other marker targets are standalone assets only when they are
        # non-null and not pure-comment text. Include dependencies are consumed
        # by their script root and must not be emitted independently.
        for pair in fire_pairs:
            ti=pair['target_index']
            if ti>=len(arc['entries']) or ti in fire_script_roots or ti in fire_include_dependencies:
                continue
            e=arc['entries'][ti]; data=e.get('data',b'')
            if not data or (len(data)<=4 and not data.strip(b'\x00')):
                continue
            text=None
            if b'\x00' not in data[:1024]:
                try: text=_pbo_decode_text(data)
                except Exception: text=None
            if text is not None and _pbo_is_pure_comment_or_empty(text):
                continue
            if text is not None and _fire_looks_like_dayz_script(text):
                continue
            fire_trusted.add(ti)
    # Mikero/DePbo cyrillic-mangle proof is emitted by T005 recovery.  The
    # map contains only source bridges and assets that were independently
    # resolved from the recovered config, so fake-extension decoys never enter
    # P3D/RVMAT verification merely because their header name lies.
    mikero_matched,mikero_confidence,mikero_evidence=_mikero_evidence(arc)
    mikero_map=None; mikero_trusted=set(); mikero_bridge_leaf_indices=set(); mikero_errors=[]; mikero_asset_ok=0; mikero_bridge_ok=0; mikero_valid_ogg=0; mikero_valid_ogg_ok=0; mikero_clean_ptc=0; mikero_clean_ptc_ok=0
    mikero_map_path=recovery_dir/MIKERO_MAP_FILE
    if mikero_matched and mikero_map_path.exists():
        try:
            mikero_map=json.loads(mikero_map_path.read_text(encoding='utf-8'))
            mikero_trusted=set(int(x) for x in mikero_map.get('trusted_indices',[]))
            # T005 deliberately hides real text/source leaves behind arbitrary
            # binary-looking extensions (including .p3d).  Those bridge leaves
            # are independently byte/semantic-verified below and must not be
            # misclassified as embedded models solely from the forged suffix.
            mikero_bridge_leaf_indices=set()
            for br in mikero_map.get('bridge_roots',[]):
                li=int(br.get('leaf_index',-1))
                if not (0 <= li < len(arc['entries'])):
                    continue
                leaf_name=arc['entries'][li].get('decoded_name','')
                if leaf_name.lower().endswith('.p3d'):
                    mikero_bridge_leaf_indices.add(li)
            for rec in mikero_map.get('rewritten_asset_references',[]):
                idx=int(rec.get('index',-1))
                if idx<0 or idx>=len(arc['entries']):
                    mikero_errors.append(f"asset index out of range: {idx}"); continue
                src=arc['entries'][idx].get('data',b'')
                rel=_pbo_safe_rel(rec.get('recovered_path',''))
                target=recovery_dir/'recovered_source'/rel
                if not target.exists():
                    mikero_errors.append(f"missing mapped asset: {rec.get('recovered_path','')}"); continue
                got=target.read_bytes()
                if got!=src or hashlib.sha1(got).hexdigest()!=rec.get('payload_sha1'):
                    mikero_errors.append(f"mapped asset mismatch: {rec.get('recovered_path','')}"); continue
                mikero_asset_ok+=1
            for br in mikero_map.get('bridge_roots',[]):
                li=int(br.get('leaf_index',-1)); rn=br.get('root_name',''); op=br.get('output_path','')
                if li<0 or li>=len(arc['entries']):
                    mikero_errors.append(f"bridge leaf index out of range: {li}"); continue
                target=recovery_dir/'recovered_source'/_pbo_safe_rel(op)
                if not target.exists():
                    mikero_errors.append(f"missing bridge output: {op}"); continue
                leaf=arc['entries'][li].get('data',b'')
                if rn.lower()=='config.cpp':
                    try:
                        original=_pbo_decode_text(leaf)
                        expected,_=_rewrite_config(original,prefix,mikero_map.get('rewritten_asset_references',[]))
                        if target.read_bytes()!=expected.encode('utf-8'):
                            mikero_errors.append('rewritten config.cpp does not match proven bridge + mapping'); continue
                    except Exception as ex:
                        mikero_errors.append(f"config bridge verification failed: {ex!r}"); continue
                else:
                    if target.read_bytes()!=leaf:
                        mikero_errors.append(f"bridge source mismatch: {op}"); continue
                mikero_bridge_ok+=1
            mapped={int(rec.get('index',-1)):rec for rec in mikero_map.get('rewritten_asset_references',[])}
            recovered_root_tmp=recovery_dir/'recovered_source'
            for idx,e in enumerate(arc['entries']):
                if e.get('kind')!='file': continue
                data=e.get('data',b'')
                kind=None
                if data.startswith(b'OggS') and _ogg_valid(data):
                    mikero_valid_ogg+=1; kind='ogg'
                elif data.startswith(b'EffectDef') and b'\0' not in data:
                    mikero_clean_ptc+=1; kind='ptc'
                if kind is None: continue
                target=None
                if idx in mapped:
                    target=recovered_root_tmp/_pbo_safe_rel(mapped[idx].get('recovered_path',''))
                else:
                    n=e.get('decoded_name','')
                    if n and not _pbo_raw_name_suspicious(e.get('name',b'')):
                        target=recovered_root_tmp/_pbo_safe_rel(n)
                ok=bool(target and target.exists() and target.read_bytes()==data)
                if kind=='ogg' and ok: mikero_valid_ogg_ok+=1
                if kind=='ptc' and ok: mikero_clean_ptc_ok+=1
            if mikero_valid_ogg_ok!=mikero_valid_ogg:
                mikero_errors.append(f'valid Ogg coverage {mikero_valid_ogg_ok}/{mikero_valid_ogg}')
            if mikero_clean_ptc_ok!=mikero_clean_ptc:
                mikero_errors.append(f'clean EffectDef/PTC coverage {mikero_clean_ptc_ok}/{mikero_clean_ptc}')
        except Exception as ex:
            mikero_errors.append(f"invalid {MIKERO_MAP_FILE}: {ex!r}")
    # KGB/JAPM emits a specialized trust map because forged extensions and
    # Windows-device-name traps are part of the protection scheme. Restrict
    # embedded asset verification to independently recovered payloads.
    kgb_map=None; kgb_trusted=set(); kgb_errors=[]
    kgb_conditional=None; kgb_conditional_verified=0; kgb_conditional_expected=0
    kgb_map_path=recovery_dir/KGB_MAP_FILE
    if kgb_map_path.exists():
        try:
            kgb_map=json.loads(kgb_map_path.read_text(encoding='utf-8'))
            kgb_trusted=set(int(x) for x in kgb_map.get('trusted_indices',[]))
            real=set(int(x) for x in kgb_map.get('real_p3d_indices',[]))
            decoys=set(int(x) for x in kgb_map.get('decoy_p3d_indices',[]))
            if real & decoys:
                kgb_errors.append('real/decoy P3D sets overlap')
            for idx in real:
                if not (0 <= idx < len(arc['entries'])) or arc['entries'][idx].get('data',b'')[:4] not in (b'MLOD',b'ODOL'):
                    kgb_errors.append(f'invalid real P3D index: {idx}')
            for _original,canonical in (kgb_map.get('module_output_map') or {}).items():
                root=recovery_dir/'recovered_source'/Path(*str(canonical).replace('/','\\').split('\\'))
                if not root.exists():
                    kgb_errors.append(f'missing canonical module root: {canonical}')
            kgb_conditional=kgb_map.get('conditional_deobfuscation')
            if kgb_conditional is not None:
                if (kgb_conditional.get('scheme')!='cfgmods-defines-conditional-v1' or
                        kgb_conditional.get('applied') is not True):
                    kgb_errors.append('invalid conditional deobfuscation metadata')
                else:
                    # Reconstruct the pre-deobfuscation source tree again from
                    # the original PBO and independently compare the complete
                    # transformed tree. This proves both branch selection and
                    # semantic renaming; the recovery map alone is not trusted.
                    try:
                        with tempfile.TemporaryDirectory(prefix='dayz_kgb_verify_') as td:
                            verify_root=Path(td)
                            recover_generic_include_graph(
                                pbo_path,verify_root,archive=arc,write_raw=False,
                                module_output_map=kgb_map.get('module_output_map') or {})
                            replay=_apply_kgb_conditional_deobfuscation(
                                verify_root/'recovered_source')
                            if replay is None:
                                kgb_errors.append('conditional deobfuscation replay was not applicable')
                            else:
                                scalar_fields=(
                                    'scheme','defined_macros_sha256','candidate_files',
                                    'recovered_scripts','suppressed_empty_decoys',
                                    'total_conditions','config_input_sha256',
                                    'config_output_sha256','config_conditionals',
                                )
                                for field in scalar_fields:
                                    if replay.get(field)!=kgb_conditional.get(field):
                                        kgb_errors.append(
                                            f'conditional metadata mismatch: {field}')
                                expected_root=verify_root/'recovered_source'
                                actual_root=recovery_dir/'recovered_source'
                                def kgb_proof_files(root):
                                    result={}
                                    for p in root.rglob('*'):
                                        if not p.is_file():
                                            continue
                                        rel=str(p.relative_to(root)).replace('/','\\').lower()
                                        if rel!='config.cpp' and not (
                                                rel.startswith('scripts\\') and
                                                rel.endswith(('.c','.cpp'))):
                                            continue
                                        result[rel]=hashlib.sha256(p.read_bytes()).hexdigest()
                                    return result
                                expected=kgb_proof_files(expected_root)
                                actual=kgb_proof_files(actual_root)
                                kgb_conditional_expected=int(replay.get('recovered_scripts',0))
                                for rel,digest in expected.items():
                                    if actual.get(rel)!=digest:
                                        kgb_errors.append(f'conditional output mismatch: {rel}')
                                    elif rel.endswith(('.c','.cpp')) and rel.startswith('scripts\\'):
                                        kgb_conditional_verified+=1
                                unexpected_scripts=sorted(
                                    rel for rel in actual if rel.startswith('scripts\\') and
                                    rel.endswith(('.c','.cpp')) and rel not in expected)
                                for rel in unexpected_scripts:
                                    kgb_errors.append(f'unexpected conditional script: {rel}')
                    except Exception as ex:
                        kgb_errors.append('conditional replay failed: '+repr(ex))
        except Exception as ex:
            kgb_errors.append('map parse failed: '+repr(ex))
            kgb_map=None; kgb_trusted=set()

    # T007 proves that Windows-safe, binary-looking zero-byte entries are part
    # of the randomized decoy field and records every suppressed output path.
    randomized_map=None; randomized_errors=[]; randomized_filtered_ok=0
    randomized_map_path=recovery_dir/RANDOMIZED_CPRS_MAP_FILE
    if randomized_map_path.exists():
        try:
            randomized_map=json.loads(randomized_map_path.read_text(encoding='utf-8'))
            if randomized_map.get('technique_id')!='T007':
                randomized_errors.append('unexpected technique id')
            for rec in randomized_map.get('filtered_safe_empty_payloads',[]):
                idx=int(rec.get('index',-1))
                if not (0<=idx<len(arc['entries'])):
                    randomized_errors.append(f'empty decoy index out of range: {idx}'); continue
                entry=arc['entries'][idx]
                if entry.get('kind')!='file' or entry.get('data',b'')!=b'':
                    randomized_errors.append(f'entry is not an empty payload: {idx}'); continue
                target=recovery_dir/'recovered_source'/_pbo_safe_rel(rec.get('recovered_path',''))
                if target.exists():
                    randomized_errors.append(f'empty decoy leaked into source: {rec.get("recovered_path","")}'); continue
                randomized_filtered_ok+=1
        except Exception as ex:
            randomized_errors.append('map parse failed: '+repr(ex))
            randomized_map=None

    selected_technique_id=None
    selection_path=recovery_dir/'PBO_TECHNIQUE_SELECTION.json'
    if selection_path.exists():
        try:
            selection=json.loads(selection_path.read_text(encoding='utf-8'))
            selected_technique_id=selection.get('selected_id')
        except Exception:
            pass
    randomized_selected=(selected_technique_id=='T007')
    if randomized_selected and randomized_map is None:
        randomized_errors.append('selected T007 recovery did not emit its proof map')

    specialized_trusted=(pbo_tools_trusted | fire_trusted | mikero_trusted | kgb_trusted) or None
    module_dirs=[]
    # Text config.cpp hints.
    for e in arc['entries']:
        if e.get('kind')!='file': continue
        n=e.get('decoded_name','').lower()
        if n.endswith('config.cpp'):
            try:
                t=_pbo_decode_text(e.get('data',b''))
                for m in re.finditer(r'(?i)["\']([^"\']*scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))',t):
                    md=m.group(1).replace('/','\\').strip('\\')
                    # reduce prefix if present
                    pl=prefix.replace('/','\\').strip('\\')
                    if pl and md.lower().startswith(pl.lower()+'\\'): md=md[len(pl)+1:]
                    if md.lower() not in [x.lower() for x in module_dirs]: module_dirs.append(md)
            except Exception: pass
        if n.endswith('config.bin') and e.get('data',b'').startswith(b'\x00raP'):
            for md in _pbo_module_dirs_from_rap(e['data'],prefix):
                if md.lower() not in [x.lower() for x in module_dirs]: module_dirs.append(md)
    # Obfuscated packers may hide CfgMods behind include bridges, so an
    # independent raw scan can fail to rediscover module_dirs even though the
    # recovery phase resolved them.  Use its report only as a fallback hint;
    # payload integrity and include resolution are still independently checked.
    recovery_report=recovery_dir/'PBO_RECOVERY_REPORT.txt'
    recovery_text=''
    if recovery_report.exists():
        try: recovery_text=recovery_report.read_text(encoding='utf-8',errors='replace')
        except Exception: recovery_text=''
    if not module_dirs and recovery_text:
        mm=re.search(r'^Module modes:\s*(.+)$',recovery_text,re.M|re.I)
        if mm and mm.group(1).strip()!='-':
            for item in mm.group(1).split(','):
                md=item.split('=',1)[0].strip()
                if md and md.lower() not in [x.lower() for x in module_dirs]: module_dirs.append(md)

    # For ordinary recursive module trees, every substantive .c/.cpp must exist in recovered_source.
    recovered_root=recovery_dir/'recovered_source'

    # T001 and T007 do not rewrite source after the generic include-graph
    # writer. Re-run that writer from the original archive in an isolated
    # directory and compare every emitted script/config byte. Previously this
    # verifier checked only path existence, so a placeholder could still be
    # reported as SEMANTIC-EXACT-INCLUDE-GRAPH.
    generic_replay_errors=[]; generic_replay_verified=0; generic_replay_expected=0
    generic_replay_applicable=selected_technique_id in ('T001','T007')
    if selected_technique_id is None:
        # Direct API callers can omit the selection wrapper. Infer T001 only
        # when no specialized format evidence or proof map is present.
        generic_replay_applicable=not (
            pbo_tools_pairs or fire_pairs or mikero_matched or
            kgb_map is not None or randomized_map is not None)
    if generic_replay_applicable:
        try:
            with tempfile.TemporaryDirectory(prefix='dayz_generic_replay_verify_') as td:
                replay_root=Path(td)
                recover_generic_include_graph(
                    pbo_path,replay_root,archive=arc,write_raw=False,
                    suppress_empty_payloads=(selected_technique_id=='T007'))
                (generic_replay_verified,generic_replay_expected,
                 generic_replay_errors)=_compare_generic_source_replay(
                    replay_root/'recovered_source',recovered_root)
        except Exception as ex:
            generic_replay_errors.append('generic source replay failed: '+repr(ex))

    # Fire Packer destroys source paths.  Verify every proven module script
    # root against the fully expanded source, including roots whose direct body
    # is only #include scaffolding.
    fire_semantic_name_results=[]
    if fire_pairs and recovered_root.exists():
        used_by_module={}
        for pair in fire_pairs:
            ti=pair['target_index']
            if ti not in fire_script_roots: continue
            e=arc['entries'][ti]; name=e.get('decoded_name','')
            md=_fire_module_from_target(name)
            if not md or not name.lower().endswith(('.c','.cpp')): continue
            info=fire_script_roots[ti]
            expanded=info['expanded']; visited=info['visited']
            ext='.cpp' if name.lower().endswith('.cpp') else '.c'
            used=used_by_module.setdefault(md.lower(),set())
            semantic_name,symbols=_fire_semantic_script_name(expanded,ext,used)
            if semantic_name is None:
                fire_semantic_name_results.append((name,None,False,'not-unambiguous'))
                continue
            used.add(semantic_name.lower())
            rel=Path(*md.split('\\'))/semantic_name
            target=recovered_root/rel
            ok=target.exists()
            if ok:
                try:
                    root_text=_pbo_decode_text(e.get('data',b''))
                    if info.get('expected_bytes') is not None:
                        ok=(target.read_bytes()==info['expected_bytes'])
                    elif re.search(r'^\s*#include\s*[<"]',root_text,re.I|re.M):
                        ok=(target.read_bytes()==expanded.encode('utf-8'))
                    else:
                        ok=(target.read_bytes()==e.get('data',b''))
                except Exception:
                    ok=False
            fire_semantic_name_results.append((name,str(rel).replace('/','\\'),ok,symbols[0] if symbols else ''))

    # Independently verify every clean standalone RaP RVMAT converted to text.
    # Specialized packers restrict verification to their proven target entries;
    # generic/plain recovery verifies all named payloads.
    clean_rap_rvmat_results=[]
    if recovered_root.exists():
        for idx,e in enumerate(arc['entries']):
            if e.get('kind')!='file': continue
            if specialized_trusted is not None and idx not in specialized_trusted: continue
            name=e.get('decoded_name','')
            data=e.get('data',b'')
            if not name.lower().endswith('.rvmat') or not data.startswith(b'\x00raP'):
                continue
            # UTF-8 damaged RVMATs use the forensic/source-proof path instead.
            if b'\xef\xbf\xbd' in data:
                continue
            target=recovered_root/_pbo_safe_rel(name)
            if not target.exists():
                clean_rap_rvmat_results.append((name,False,'missing recovered RVMAT'))
                continue
            current=target.read_bytes()
            if current.startswith(b'\x00raP'):
                clean_rap_rvmat_results.append((name,False,'still binary RaP'))
                continue
            try:
                text=current.decode('utf-8')
                ok,_=verify_clean_rap_rvmat_text(data,text)
                clean_rap_rvmat_results.append((name,bool(ok),'SEMANTIC-EXACT-CLEAN-RAP' if ok else 'semantic mismatch'))
            except Exception as ex:
                clean_rap_rvmat_results.append((name,False,repr(ex)))

    # Independent negative verification: exact recovery must not leak packer
    # junk or text payloads disguised as binary assets into recovered_source.
    leaked_decoys=[]
    leaked_type_mismatches=[]
    binary_asset_exts={'.paa','.pac','.p3d','.ogg','.wav','.wss','.edds','.rtm','.anm','.bin'}
    if recovered_root.exists():
        for e in arc['entries']:
            if e.get('kind')!='file': continue
            n=e.get('decoded_name','').replace('/','\\')
            if not n or n.lower().endswith(('.c','.cpp')): continue
            data=e.get('data',b'')
            if not data: continue
            sample=data[:1024]
            if b'\0' in sample: continue
            try:
                text=_pbo_decode_text(data)
            except Exception:
                continue
            printable=sum(1 for ch in text[:1024] if ch.isprintable() or ch in '\r\n\t')
            if text[:1024] and printable/max(1,len(text[:1024]))<0.85: continue
            try:
                rel=_pbo_safe_rel(n)
            except Exception:
                continue
            if not (recovered_root/rel).exists(): continue
            suffix=Path(n).suffix.lower()
            if suffix in binary_asset_exts:
                leaked_type_mismatches.append(str(rel).replace('/','\\'))
            elif _pbo_is_pure_comment_or_empty(text):
                leaked_decoys.append(str(rel).replace('/','\\'))

        # Fire Packer include dependencies are implementation fragments, not
        # authoring files.  Their original fake paths must never survive in the
        # recovered tree.  This independently catches both substantive fragments
        # (e.g. Dump1.rvmat containing MissionServer) and comment-only junk.
        for j in fire_include_dependencies:
            if j>=len(arc['entries']): continue
            e=arc['entries'][j]; n=e.get('decoded_name','')
            if not n: continue
            try: rel=_pbo_safe_rel(n)
            except Exception: continue
            if not (recovered_root/rel).exists(): continue
            item=str(rel).replace('/','\\')
            if j in fire_substantive_dependencies:
                leaked_type_mismatches.append(item)
            else:
                leaked_decoys.append(item)

    # Keep report counts stable when the same leaked path is caught by more
    # than one independent rule.
    leaked_decoys=list(dict.fromkeys(leaked_decoys))
    leaked_type_mismatches=list(dict.fromkeys(leaked_type_mismatches))

    coverage=[]; missing=[]
    for md in module_dirs:
        candidates=[]; suspicious=0
        mdl=md.lower().rstrip('\\')
        for e in arc['entries']:
            if e.get('kind')!='file': continue
            n=e.get('decoded_name','').replace('/','\\'); nl=n.lower()
            if (nl.startswith(mdl+'\\') or nl==mdl) and nl.endswith(('.c','.cpp')):
                candidates.append(e)
                if _pbo_raw_name_suspicious(e.get('name',b'')): suspicious+=1
        obfuscated=((suspicious/len(candidates))>=0.10 if candidates else False) or len(candidates)>100
        if obfuscated:
            coverage.append((md,'include-graph',len(candidates),None))
            continue
        expected=0; present=0
        for e in candidates:
            try:
                text=_pbo_decode_text(e.get('data',b''))
            except Exception: continue
            if not _pbo_has_substantive_script(text): continue
            expected+=1
            rel=_pbo_safe_rel(e.get('decoded_name',''))
            target=recovered_root/rel
            if target.exists(): present+=1
            else: missing.append(str(rel).replace('/','\\'))
        coverage.append((md,'recursive',expected,present))
    if mikero_matched and mikero_map:
        # Canonical .c/.cpp bridge roots are the authoring module files.  Their
        # hidden fake-extension leaves are implementation details of the packer.
        counts={}
        for br in mikero_map.get('bridge_roots',[]):
            op=br.get('output_path','').replace('/','\\')
            m=re.match(r'(?i)(scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))(?:\\|$)',op)
            if m: counts[m.group(1).lower()]=counts.get(m.group(1).lower(),0)+1
        # Keep declared empty modules visible as 0/0 when possible.
        for md in module_dirs:
            counts.setdefault(md.lower(),0)
        coverage=[(md,'mikero-cyrillic-mangle',count,count) for md,count in sorted(counts.items())]
        missing=[]
    elif pbo_tools_pairs:
        # Scrambled filenames deliberately make recursive candidate counts
        # meaningless. Marker roots + zero unresolved include chains are the
        # authoritative script coverage for this scheme.
        marker_module_counts={}
        for pair in pbo_tools_pairs:
            if pair['ext'] not in ('c','cpp'): continue
            md=_pbo_tools_module_from_name(pair['target_name'])
            if md: marker_module_counts[md]=marker_module_counts.get(md,0)+1
        coverage=[(md,'pbo-tools-marker',count,count) for md,count in sorted(marker_module_counts.items())]
        missing=[]
    elif fire_pairs:
        # Fire Packer deliberately destroys script filenames.  Only non-empty,
        # substantive targets immediately following a proven marker group are
        # loaded source roots; the COM/LPT/AUX header path is not an authoring
        # filename and must not be required in recovered_source.
        fire_module_counts={}
        for pair in fire_pairs:
            ti=pair['target_index']
            if ti not in fire_trusted: continue
            name=arc['entries'][ti].get('decoded_name','')
            md=_fire_module_from_target(name)
            if md and name.lower().endswith(('.c','.cpp')):
                fire_module_counts[md]=fire_module_counts.get(md,0)+1
        coverage=[(md,'fire-packer-marker',count,count) for md,count in sorted(fire_module_counts.items())]
        missing=[]
    elif kgb_conditional is not None:
        kgb_module_counts={}
        for rec in kgb_conditional.get('script_records',[]):
            op=rec.get('recovered_path','').replace('/','\\')
            m=re.match(r'(?i)(scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission))(?:\\|$)',op)
            if m:
                key=m.group(1).lower()
                kgb_module_counts[key]=kgb_module_counts.get(key,0)+1
        coverage=[(md,'kgb-japm-conditional',count,count)
                  for md,count in sorted(kgb_module_counts.items())]
        missing=[]
    config_entries=[]
    for e in arc['entries']:
        if e.get('kind')!='file': continue
        n=e.get('decoded_name','')
        data=e.get('data',b'')
        if n.lower().endswith('.bin') and data.startswith(b'\x00raP'):
            config_entries.append((n,data))
    config_results=[]
    config_errors=[]
    if cfgconvert:
        for n,data in config_entries:
            try:
                config_results.append(verify_config_bin_with_cfgconvert(data,n,cfgconvert,recovery_dir))
            except Exception as ex:
                config_errors.append((n,repr(ex)))
    p3d_results=_pbo_verify_embedded_p3ds(
        arc,recovery_dir,trusted_indices=specialized_trusted,
        excluded_indices=mikero_bridge_leaf_indices,
    )
    p3d_bad=[x for x in p3d_results if x[1] not in ('SEMANTIC-EXACT','MLOD-ALREADY')]
    byte_exact=(arc['archive_sha_ok'] is True and not payload_errors and cprs_ok==cprs_total)
    recursive_exact=(not missing and all((mode!='recursive' or exp==got) for _md,mode,exp,got in coverage))
    report_unresolved=None; report_payload_errors=None; report_recovered=None
    if recovery_text:
        m=re.search(r'^Unresolved includes:\s*(\d+)',recovery_text,re.M|re.I); report_unresolved=int(m.group(1)) if m else None
        m=re.search(r'^Payload errors:\s*(\d+)',recovery_text,re.M|re.I); report_payload_errors=int(m.group(1)) if m else None
        m=re.search(r'^Recovered source files:\s*(\d+)',recovery_text,re.M|re.I); report_recovered=int(m.group(1)) if m else None
    graph_exact=((report_unresolved in (None,0)) and (report_payload_errors in (None,0)))
    configs_verified=(not config_entries) or (bool(cfgconvert) and not config_errors and len(config_results)==len(config_entries) and all(x['semantic'] for x in config_results))
    clean_rap_rvmats_exact=all(ok for _name,ok,_detail in clean_rap_rvmat_results)
    fire_semantic_names_exact=all(ok for _src,dst,ok,_detail in fire_semantic_name_results if dst is not None)
    mikero_exact=(not mikero_matched) or (mikero_map is not None and not mikero_errors and mikero_asset_ok==len(mikero_map.get('rewritten_asset_references',[])) and mikero_bridge_ok==len(mikero_map.get('bridge_roots',[])))
    kgb_exact=(kgb_map is None) or not kgb_errors
    randomized_exact=(not randomized_selected) or (randomized_map is not None and not randomized_errors and
        randomized_filtered_ok==len(randomized_map.get('filtered_safe_empty_payloads',[])))
    generic_replay_exact=not generic_replay_errors
    base_exact=(byte_exact and recursive_exact and graph_exact and generic_replay_exact and not p3d_bad and not leaked_decoys and not leaked_type_mismatches and clean_rap_rvmats_exact and fire_semantic_names_exact and mikero_exact and kgb_exact and randomized_exact)
    semantic_exact=(base_exact and configs_verified)
    has_obfuscated_graph=any(mode in ('include-graph','pbo-tools-marker','fire-packer-marker','mikero-cyrillic-mangle','kgb-japm-conditional') for _md,mode,_exp,_got in coverage) or (cprs_total>0 and (report_recovered or 0)>0)
    if semantic_exact and (pbo_tools_pairs or fire_pairs or mikero_matched):
        status='SEMANTIC-EXACT-INCLUDE-GRAPH'
    elif semantic_exact and has_obfuscated_graph:
        status='SEMANTIC-EXACT-INCLUDE-GRAPH'
    elif semantic_exact:
        status='SEMANTIC-EXACT'
    elif base_exact and config_entries and not cfgconvert:
        status='P3D+SCRIPTS-EXACT / CONFIGS-UNVERIFIED'
    else:
        status='WARNING/LOSS'
    lines=[
        'PBO recovery/equivalence verification',
        f'Converter: {SCRIPT_VERSION}',
        f'Source: {pbo_path.resolve()}',
        f'Prefix: {prefix}',
        f'Archive trailer SHA1: {"BYTE-INTEGRITY-OK" if arc["archive_sha_ok"] is True else arc["archive_sha_ok"]}',
        f'Payload decode: {"CHECKSUM-EXACT" if not payload_errors else "ERROR"}',
        f'Cprs checksums: {cprs_ok}/{cprs_total}',
        f'Module coverage missing files: {len(missing)}',
        f'Include graph unresolved: {report_unresolved if report_unresolved is not None else "unknown"}',
        f'Recovery payload errors: {report_payload_errors if report_payload_errors is not None else "unknown"}',
        f'Generic source replay verified: {generic_replay_verified}/{generic_replay_expected}' if generic_replay_applicable else 'Generic source replay verified: not-applicable',
        f'Generic source replay mismatches: {len(generic_replay_errors)}',
        f'Recovered decoy leakage: {len(leaked_decoys)}',
        f'Recovered type-mismatch leakage: {len(leaked_type_mismatches)}',
        f'PBO Tools marker roots verified: {len(pbo_tools_pairs) if pbo_tools_pairs else 0}',
                f'PBO Tools marker recovery: {"EXACT-INCLUDE-GRAPH" if pbo_tools_pairs and not missing and graph_exact else "not-used" if not pbo_tools_pairs else "WARNING"}',
        f'Fire Packer marker groups verified: {len(fire_pairs) if fire_pairs else 0}',
        f'Fire Packer trusted targets verified: {len(fire_trusted)}',
        f'Fire Packer marker recovery: {"EXACT-INCLUDE-GRAPH" if fire_pairs and not missing and graph_exact else "not-used" if not fire_pairs else "WARNING"}',
        f'Fire Packer semantic script names verified: {sum(1 for _src,_dst,ok,_detail in fire_semantic_name_results if _dst is not None and ok)}/{sum(1 for _src,_dst,_ok,_detail in fire_semantic_name_results if _dst is not None)}',
        f'Mikero cyrillic-mangle detected: {"yes" if mikero_matched else "no"}',
        f'Mikero mapped assets verified: {mikero_asset_ok}/{len(mikero_map.get("rewritten_asset_references",[])) if mikero_map else 0}',
        f'Mikero source bridges verified: {mikero_bridge_ok}/{len(mikero_map.get("bridge_roots",[])) if mikero_map else 0}',
        f'Mikero fake-extension bridge leaves excluded from P3D verification: {len(mikero_bridge_leaf_indices)}',
        f'Mikero valid Ogg payloads preserved: {mikero_valid_ogg_ok}/{mikero_valid_ogg}',
        f'Mikero clean EffectDef/PTC preserved: {mikero_clean_ptc_ok}/{mikero_clean_ptc}',
        f'Mikero mapping errors: {len(mikero_errors)}',
        f'KGB/JAPM specialized recovery: {"yes" if kgb_map is not None else "no"}',
        f'KGB/JAPM trusted payloads: {len(kgb_trusted)}',
        f'KGB/JAPM real P3Ds: {len(kgb_map.get("real_p3d_indices",[])) if kgb_map else 0}',
        f'KGB/JAPM forged P3D decoys excluded: {len(kgb_map.get("decoy_p3d_indices",[])) if kgb_map else 0}',
        f'KGB/JAPM conditional scripts verified: {kgb_conditional_verified}/{kgb_conditional_expected}',
        f'KGB/JAPM mapping errors: {len(kgb_errors)}',
        f'Randomized Cprs specialized recovery: {"yes" if randomized_selected else "no"}',
        f'Randomized Cprs empty decoys verified absent: {randomized_filtered_ok}/{len(randomized_map.get("filtered_safe_empty_payloads",[])) if randomized_map else 0}',
        f'Randomized Cprs mapping errors: {len(randomized_errors)}',
        f'Rapified RVMATs semantic-verified: {sum(1 for _name,ok,_detail in clean_rap_rvmat_results if ok)}/{len(clean_rap_rvmat_results)}',
        f'Embedded P3Ds verified: {len(p3d_results)-len(p3d_bad)}/{len(p3d_results)}',
        f'Rapified config.bin files: {len(config_entries)}',
        f'Configs semantic-verified: {sum(1 for x in config_results if x.get("semantic"))}/{len(config_entries)}' if cfgconvert else f'Configs semantic-verified: 0/{len(config_entries)} (pass --cfgconvert)',
        f'Overall: {status}','',
        'MODULE COVERAGE:'
    ]
    for md,mode,exp,got in coverage:
        if mode=='recursive': lines.append(f'- {md}: recursive {got}/{exp}')
        elif mode=='pbo-tools-marker': lines.append(f'- {md}: PBO Tools marker roots {got}/{exp}; include chains verified by recovery report')
        elif mode=='fire-packer-marker': lines.append(f'- {md}: Fire Packer trusted script roots {got}/{exp}; marker relation verified independently')
        elif mode=='mikero-cyrillic-mangle': lines.append(f'- {md}: Mikero source bridge roots {got}/{exp}; hidden leaves and rewritten asset refs verified independently')
        elif mode=='kgb-japm-conditional': lines.append(f'- {md}: KGB/JAPM conditional scripts {got}/{exp}; branches and semantic names replayed from original PBO')
        else: lines.append(f'- {md}: obfuscated/include-graph ({exp} candidate entries; reachability is verified by recovery report)')
    if p3d_results:
        lines += ['','EMBEDDED P3D:']
        for name,st,nerr,msg in p3d_results:
            lines.append(f'- {name}: {st}' + (f' ({msg})' if msg else ''))
    if fire_semantic_name_results:
        lines += ['','FIRE PACKER SCRIPT NAMES:']
        for src,dst,ok,detail in fire_semantic_name_results:
            if dst is None:
                lines.append(f'- {src}: neutral filename retained ({detail})')
            else:
                lines.append(f'- {src} => {dst}: {"VERIFIED" if ok else "MISMATCH"}; primary={detail}')
    if clean_rap_rvmat_results:
        lines += ['','RAPIFIED RVMAT:']
        for name,ok,detail in clean_rap_rvmat_results:
            lines.append(f'- {name}: {"SEMANTIC-EXACT" if ok else "WARNING"}; {detail}')
    if config_entries:
        lines += ['','CONFIG.BIN:']
        if cfgconvert:
            for x in config_results:
                lines.append(f'- {x["name"]}: {"SEMANTIC-EXACT" if x["semantic"] else "DIFFERENT"}; roundtrip-bytes={"IDENTICAL" if x["byte_equal"] else "different"}; cpp={x["recovered_cpp"]}')
            for n,er in config_errors:
                lines.append(f'- {n}: ERROR {er}')
        else:
            lines.append('- Not verified. Supply --cfgconvert "C:\\...\\CfgConvert.exe" for official BIN->CPP->BIN semantic round-trip.')
    if missing:
        lines += ['','MISSING RECURSIVE SCRIPTS:']+[f'- {x}' for x in missing]
    if leaked_decoys:
        lines += ['','RECOVERED DECOY LEAKAGE:']+[f'- {x}' for x in leaked_decoys]
    if leaked_type_mismatches:
        lines += ['','RECOVERED TYPE-MISMATCH LEAKAGE:']+[f'- {x}' for x in leaked_type_mismatches]
    if mikero_errors:
        lines += ['','MIKERO RECOVERY ERRORS:']+[f'- {x}' for x in mikero_errors]
    if kgb_errors:
        lines += ['','KGB/JAPM RECOVERY ERRORS:']+[f'- {x}' for x in kgb_errors]
    if randomized_errors:
        lines += ['','RANDOMIZED CPRS RECOVERY ERRORS:']+[f'- {x}' for x in randomized_errors]
    if generic_replay_errors:
        lines += ['','GENERIC SOURCE REPLAY ERRORS:']+[f'- {x}' for x in generic_replay_errors]
    if payload_errors:
        lines += ['','PAYLOAD ERRORS:']+[f'- [{i}] {n}: {er}' for i,n,er in payload_errors]
    # CfgConvert verification can add editable *_from_bin.cpp/config.cpp files
    # after the recovery report was written. Keep its declared source-file count
    # synchronized with the final recovered_source tree so Workbench's independent
    # physical-count audit remains exact.
    if recovery_report.exists() and recovered_root.exists():
        try:
            final_count=sum(1 for x in recovered_root.rglob('*') if x.is_file())
            rt=recovery_report.read_text(encoding='utf-8',errors='replace')
            rt=re.sub(r'^Recovered source files:\s*\d+\s*$',
                      f'Recovered source files: {final_count}',rt,flags=re.M|re.I)
            recovery_report.write_text(rt,encoding='utf-8')
        except Exception:
            pass

    report=recovery_dir/'PBO_EQUIVALENCE_VERIFICATION.txt'
    report.write_text('\n'.join(lines)+'\n',encoding='utf-8')
    return dict(status=status,byte_exact=byte_exact,semantic_exact=semantic_exact,
                cprs_ok=cprs_ok,cprs_total=cprs_total,missing=missing,p3d=p3d_results,
                configs=config_results,config_errors=config_errors,config_count=len(config_entries),
                leaked_decoys=leaked_decoys,leaked_type_mismatches=leaked_type_mismatches,
                generic_replay_errors=generic_replay_errors,
                generic_replay_verified=generic_replay_verified,
                generic_replay_expected=generic_replay_expected,report=report)
