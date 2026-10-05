from pathlib import Path
import json, re, hashlib, os, tempfile

from .base import DetectionResult, RecoveryTechnique
from .generic_include_graph import recover_generic_include_graph
from .fire_packer import _semantic_script_name, _looks_like_dayz_script, _code_skeleton
from ..pbo.core import (
    pbo_entry_data, _pbo_decode_name, _pbo_decode_text, _pbo_raw_name_suspicious,
    _pbo_is_pure_comment_or_empty,
)

KGB_MAP_FILE = 'KGB_JAPM_MAP.json'

_KGB_NAME_RE = re.compile(r'KGB_DF_[A-Za-z0-9_]+')
_KGB_DEFINE_BLOCK_RE = re.compile(
    r'\bdefines\s*\[\s*\]\s*=\s*\{(.*?)\}\s*;', re.I | re.S)
_CONDITIONAL_RE = re.compile(
    r'#[ \t]*(?:(ifdef|ifndef)[ \t]+([A-Za-z_][A-Za-z0-9_]*)|(else|endif)\b)',
    re.I)


class KgbConditionalError(ValueError):
    pass


def _extract_kgb_defines(config_text):
    """Read the macro truth table emitted by KGB in CfgMods.defines[]."""
    defined=set()
    for block in _KGB_DEFINE_BLOCK_RE.findall(config_text or ''):
        defined.update(_KGB_NAME_RE.findall(block))
    return defined


def _evaluate_kgb_conditionals(text, defined_macros):
    """Evaluate KGB's inline/nested conditional token stream.

    KGB stores the true macro set in CfgMods.defines[]. Directives for other
    environments (for example DOXYGEN) are preserved verbatim. This is a
    conditional evaluator, not a source-code heuristic.
    """
    defined=set(defined_macros or ())
    out=[]; pos=0; emit=True; stack=[]; kgb_directives=0; unknown_directives=0
    for match in _CONDITIONAL_RE.finditer(text or ''):
        if emit:
            out.append(text[pos:match.start()])
        op=(match.group(1) or match.group(3)).lower()
        macro=match.group(2)
        if op in ('ifdef','ifndef'):
            known=bool(macro and macro.startswith('KGB_DF_'))
            if known:
                condition=(macro in defined) if op=='ifdef' else (macro not in defined)
                stack.append((True,emit,condition,False))
                emit=emit and condition
                kgb_directives+=1
            else:
                if emit:
                    out.append(match.group(0))
                stack.append((False,emit,True,False))
                unknown_directives+=1
        elif op=='else':
            if not stack:
                raise KgbConditionalError('orphan #else')
            known,parent,condition,seen_else=stack[-1]
            if seen_else:
                raise KgbConditionalError('duplicate #else')
            stack[-1]=(known,parent,condition,True)
            if known:
                emit=parent and not condition
            elif emit:
                out.append(match.group(0))
        else:
            if not stack:
                raise KgbConditionalError('orphan #endif')
            known,parent,_condition,_seen_else=stack.pop()
            if not known and emit:
                out.append(match.group(0))
            emit=parent
        pos=match.end()
    if stack:
        raise KgbConditionalError(f'unclosed conditional block(s): {len(stack)}')
    if emit:
        out.append((text or '')[pos:])
    result=''.join(out)
    result=re.sub(r'[ \t]+(?=\r?$)','',result,flags=re.M)
    result=re.sub(r'(?:\r?\n){3,}','\n\n',result).strip()
    if result:
        result+='\n'
    return result, {
        'kgb_conditionals':kgb_directives,
        'preserved_conditionals':unknown_directives,
    }


def _sha256_text(text):
    return hashlib.sha256((text or '').encode('utf-8')).hexdigest()


def _kgb_conditional_deobfuscation_plan(recovered_root):
    """Build an all-or-nothing deobfuscation plan for the defines[] variant."""
    recovered_root=Path(recovered_root)
    config_path=recovered_root/'config.cpp'
    if not config_path.exists():
        return None
    config_text=config_path.read_text(encoding='utf-8',errors='replace')
    defined=_extract_kgb_defines(config_text)
    scripts_root=recovered_root/'scripts'
    candidates=[]
    if scripts_root.exists():
        for path in sorted(scripts_root.rglob('*'),key=lambda p:str(p).lower()):
            if not path.is_file() or path.suffix.lower() not in ('.c','.cpp'):
                continue
            source=path.read_text(encoding='utf-8',errors='replace')
            if re.search(r'#[ \t]*ifn?def[ \t]+KGB_DF_',source,re.I):
                candidates.append((path,source))

    # Strict subtype gate: ordinary KGB/JAPM archives retain the established
    # include-graph output. Only the dense CfgMods.defines[] token-stream
    # variant enters this path.
    total_conditions=sum(len(re.findall(r'#[ \t]*ifn?def[ \t]+KGB_DF_',s,re.I))
                         for _p,s in candidates)
    if len(defined)<4 or not candidates or total_conditions<32:
        return None

    transformed=[]; empty=[]
    for path,source in candidates:
        clean,stats=_evaluate_kgb_conditionals(source,defined)
        if re.search(r'#[ \t]*ifn?def[ \t]+KGB_DF_',clean,re.I):
            raise KgbConditionalError(f'KGB conditional survived: {path}')
        rel=path.relative_to(recovered_root)
        record={
            'original_path':str(rel).replace('/','\\'),
            'input_sha256':_sha256_text(source),
            **stats,
        }
        if not clean.strip():
            record['suppressed_as_empty_decoy']=True
            empty.append((path,record))
            continue
        skeleton=_code_skeleton(clean)
        if skeleton.count('{')!=skeleton.count('}') or not _looks_like_dayz_script(clean):
            raise KgbConditionalError(f'deobfuscated script failed structural checks: {path}')
        semantic_name,symbols=_semantic_script_name(clean,path.suffix.lower(),set())
        if semantic_name is None:
            raise KgbConditionalError(f'no unambiguous semantic filename: {path}')
        record.update({
            'output_sha256':_sha256_text(clean),
            'declared_symbols':list(symbols),
        })
        transformed.append((path,clean,semantic_name,record))

    if not transformed:
        return None

    # Assign deterministic, meaningful names while retaining distinct partial
    # modded-class files (CarScript.c, CarScript__part_02.c, ...).
    used_by_dir={}
    candidate_paths={p.resolve() for p,_s in candidates}
    for path in scripts_root.rglob('*'):
        if path.is_file() and path.resolve() not in candidate_paths:
            used_by_dir.setdefault(path.parent.resolve(),set()).add(path.name.lower())
    planned=[]
    for path,clean,semantic_name,record in transformed:
        used=used_by_dir.setdefault(path.parent.resolve(),set())
        candidate=semantic_name; serial=1
        while candidate.lower() in used:
            serial+=1
            p=Path(semantic_name)
            candidate=f'{p.stem}__part_{serial:02d}{p.suffix}'
        used.add(candidate.lower())
        target=path.with_name(candidate)
        record['recovered_path']=str(target.relative_to(recovered_root)).replace('/','\\')
        planned.append((path,target,clean,record))

    clean_config,config_stats=_evaluate_kgb_conditionals(config_text,defined)
    if ('CfgPatches' not in clean_config or 'CfgMods' not in clean_config or
            re.search(r'#[ \t]*ifn?def[ \t]+KGB_DF_',clean_config,re.I)):
        raise KgbConditionalError('deobfuscated config.cpp failed structural checks')

    return {
        'defined_macros':sorted(defined),
        'defined_macros_sha256':hashlib.sha256(
            '\n'.join(sorted(defined)).encode('utf-8')).hexdigest(),
        'candidate_files':len(candidates),
        'total_conditions':total_conditions,
        'planned':planned,
        'empty':empty,
        'config_path':config_path,
        'config_input_sha256':_sha256_text(config_text),
        'config_output_sha256':_sha256_text(clean_config),
        'config_stats':config_stats,
        'clean_config':clean_config,
    }


def _apply_kgb_conditional_deobfuscation(recovered_root):
    """Apply the proven plan while preserving the old output on any proof failure."""
    recovered_root=Path(recovered_root)
    try:
        plan=_kgb_conditional_deobfuscation_plan(recovered_root)
    except (OSError,UnicodeError,KgbConditionalError):
        return None
    if plan is None:
        return None

    all_original=[x[0] for x in plan['planned']]+[x[0] for x in plan['empty']]
    records=[x[3] for x in plan['planned']]+[x[1] for x in plan['empty']]
    with tempfile.TemporaryDirectory(prefix='.kgb_deobf_',dir=str(recovered_root)) as td:
        stage=Path(td)
        for _src,target,clean,_record in plan['planned']:
            staged=stage/target.relative_to(recovered_root)
            staged.parent.mkdir(parents=True,exist_ok=True)
            staged.write_text(clean,encoding='utf-8')
        (stage/'config.cpp').write_text(plan['clean_config'],encoding='utf-8')
        # Nothing is removed until every transformed output has been written.
        for source in all_original:
            source.unlink()
        for staged in sorted(stage.rglob('*')):
            if not staged.is_file():
                continue
            target=recovered_root/staged.relative_to(stage)
            target.parent.mkdir(parents=True,exist_ok=True)
            os.replace(staged,target)

    return {
        'scheme':'cfgmods-defines-conditional-v1',
        'applied':True,
        'defined_macros':plan['defined_macros'],
        'defined_macros_sha256':plan['defined_macros_sha256'],
        'candidate_files':plan['candidate_files'],
        'recovered_scripts':len(plan['planned']),
        'suppressed_empty_decoys':len(plan['empty']),
        'total_conditions':plan['total_conditions'],
        'config_input_sha256':plan['config_input_sha256'],
        'config_output_sha256':plan['config_output_sha256'],
        'config_conditionals':plan['config_stats']['kgb_conditionals'],
        'script_records':records,
    }


def _entry_name(entry):
    return entry.get('decoded_name') or _pbo_decode_name(entry.get('name', b''))


def _kgb_module_mapping(archive):
    """Return original CfgMods module roots mapped to conventional DayZ roots."""
    prefix=''
    for k,v in archive.get('properties',[]):
        if _pbo_decode_name(k).lower()=='prefix':
            prefix=_pbo_decode_name(v).replace('/','\\').strip('\\')
            break
    classes=(('gameScriptModule','scripts\\3_Game'),
             ('worldScriptModule','scripts\\4_World'),
             ('missionScriptModule','scripts\\5_Mission'))
    for entry in archive.get('entries',[]):
        if entry.get('kind')!='file':
            continue
        try:
            data=entry.get('data') if 'data' in entry else pbo_entry_data(archive,entry)
        except Exception:
            continue
        if not data or b'CfgMods' not in data or b'KGB_DF_' not in data:
            continue
        try:
            text=_pbo_decode_text(data)
        except Exception:
            continue
        mapping={}
        for class_name,target in classes:
            m=re.search(r'class\s+'+re.escape(class_name)+r'\s*\{.*?files\s*\[\s*\]\s*=\s*\{\s*"([^"]+)"',text,re.I|re.S)
            if not m:
                continue
            q=m.group(1).replace('/','\\').strip('\\').rstrip('\\')
            if prefix and q.lower().startswith(prefix.lower()+'\\'):
                q=q[len(prefix)+1:]
            if q:
                mapping[q.lower()]=target
        if mapping:
            return mapping
    return {}


def _kgb_evidence(archive):
    kgb_names=0; japm_names=0; wildcard=0; owner_marker=False; macro_marker=False
    file_count=0; suspicious=0
    for entry in archive.get('entries',[]):
        if entry.get('kind')!='file':
            continue
        file_count+=1
        raw=entry.get('name',b'')
        if _pbo_raw_name_suspicious(raw):
            suspicious+=1
        name=_entry_name(entry).replace('/','\\')
        nl=name.lower()
        if '.kgb' in nl:
            kgb_names+=1
        if nl.startswith('__japm__\\') or '\\__japm__\\' in nl:
            japm_names+=1
        if '*.*' in name:
            wildcard+=1
        # Read only a handful of highly indicative entries; this keeps detection cheap.
        if ('.kgb' in nl or name.lower()=='config.cpp') and not (owner_marker and macro_marker):
            try:
                data=entry.get('data') if 'data' in entry else pbo_entry_data(archive,entry)
            except Exception:
                data=b''
            if b'Property of KGB or Partners' in data:
                owner_marker=True
            if b'KGB_DF_' in data:
                macro_marker=True
    ratio=(suspicious/file_count) if file_count else 0.0
    matched=(kgb_names>=3 and (japm_names>=1 or owner_marker or macro_marker))
    confidence=0.0
    if matched:
        confidence=0.995 if (owner_marker or macro_marker) else 0.97
    evidence=(
        f'kgb_named_entries={kgb_names}',
        f'japm_entries={japm_names}',
        f'wildcard_entries={wildcard}',
        f'suspicious_name_ratio={ratio:.3f}',
        f'kgb_owner_marker={owner_marker}',
        f'kgb_macro_marker={macro_marker}',
    )
    return matched,confidence,evidence


class KgbJapmTechnique(RecoveryTechnique):
    technique_id = 'T006'
    technique_version = '1.1.0'
    family = 'kgb-japm'
    name = 'kgb-japm'
    priority = 700
    automatic_threshold = 0.95
    fallback = False

    def detect(self,archive):
        matched,confidence,evidence=_kgb_evidence(archive)
        return DetectionResult(self.name,matched,confidence,evidence)

    def recover(self,pbo_path,out_dir,archive,write_raw=False):
        out_dir=Path(out_dir)
        mapping=_kgb_module_mapping(archive)
        result=recover_generic_include_graph(
            pbo_path,out_dir,archive=archive,write_raw=write_raw,module_output_map=mapping)

        # Newer KGB variants encode a macro truth table in CfgMods.defines[]
        # and use it to select individual tokens. This post-pass is strictly
        # gated and all-or-nothing; older KGB/JAPM archives keep the v1.0 output.
        conditional_deobf=_apply_kgb_conditional_deobfuscation(out_dir/'recovered_source')

        # Build a specialized trust map. KGB/JAPM deliberately creates hundreds
        # of text decoys with forged binary extensions; only independently sane
        # header paths and payloads with real P3D magic are promoted to the map.
        trusted=set(); real_p3d=[]; decoy_p3d=[]; kgb_script_payloads=[]
        binary_exts={'.paa','.pac','.p3d','.ogg','.wav','.wss','.edds','.rtm','.anm','.bin'}
        for idx,entry in enumerate(archive.get('entries',[])):
            if entry.get('kind')!='file':
                continue
            name=_entry_name(entry).replace('/','\\')
            entry['decoded_name']=name
            try:
                data=entry.get('data') if 'data' in entry else pbo_entry_data(archive,entry)
                entry['data']=data
            except Exception:
                continue
            suffix=Path(name).suffix.lower()
            magic=data[:4]
            if suffix=='.p3d':
                if magic in (b'MLOD',b'ODOL'):
                    real_p3d.append(idx)
                else:
                    decoy_p3d.append(idx)
            if '.kgb' in name.lower() and b'KGB_DF_' in data[:4096]:
                kgb_script_payloads.append(idx)
            if _pbo_raw_name_suspicious(entry.get('name',b'')):
                continue
            if suffix in binary_exts and data and b'\0' not in data[:1024]:
                try:
                    text=_pbo_decode_text(data)
                    printable=sum(1 for ch in text[:1024] if ch.isprintable() or ch in '\r\n\t')
                    if text[:1024] and printable/max(1,len(text[:1024]))>=0.85:
                        continue
                except Exception:
                    pass
            if data:
                trusted.add(idx)
        trusted.update(real_p3d)

        recovered_root=out_dir/'recovered_source'
        physical_files=sum(1 for p in recovered_root.rglob('*') if p.is_file()) if recovered_root.exists() else 0
        map_data={
            'scheme':'KGB/JAPM',
            'version':1,
            'module_output_map':mapping,
            'trusted_indices':sorted(trusted),
            'real_p3d_indices':sorted(real_p3d),
            'decoy_p3d_indices':sorted(decoy_p3d),
            'kgb_script_payload_indices':sorted(kgb_script_payloads),
            'physical_recovered_files':physical_files,
        }
        if conditional_deobf is not None:
            map_data['conditional_deobfuscation']=conditional_deobf
        (out_dir/KGB_MAP_FILE).write_text(json.dumps(map_data,ensure_ascii=False,indent=2),encoding='utf-8')

        report=out_dir/'PBO_RECOVERY_REPORT.txt'
        if report.exists():
            text=report.read_text(encoding='utf-8',errors='replace')
            # Keep the declared count synchronized with collision-safe physical output.
            text=re.sub(r'^Recovered source files:\s*\d+\s*$',f'Recovered source files: {physical_files}',text,flags=re.M|re.I)
            extra=(
                f'KGB/JAPM protection detected: yes\n'
                f'KGB/JAPM module roots canonicalized: {len(mapping)}\n'
                f'KGB/JAPM real P3Ds: {len(real_p3d)}\n'
                f'KGB/JAPM forged P3D decoys ignored: {len(decoy_p3d)}\n'
                f'KGB/JAPM trusted payloads: {len(trusted)}\n'
            )
            if conditional_deobf is not None:
                extra += (
                    f'KGB/JAPM conditional scripts recovered: {conditional_deobf["recovered_scripts"]}\n'
                    f'KGB/JAPM conditional empty decoys suppressed: {conditional_deobf["suppressed_empty_decoys"]}\n'
                    f'KGB/JAPM conditional branches evaluated: {conditional_deobf["total_conditions"]}\n'
                )
                # Synchronize the detailed source list with renamed/suppressed scripts.
                for rec in conditional_deobf['script_records']:
                    old='- '+rec['original_path']
                    if rec.get('suppressed_as_empty_decoy'):
                        text=re.sub(r'^'+re.escape(old)+r'\s*$', '', text, flags=re.M)
                    else:
                        text=re.sub(r'^'+re.escape(old)+r'\s*$',
                                    lambda _m, value='- '+rec['recovered_path']: value,
                                    text,flags=re.M)
            # Put machine-readable summary before RECOVERED SOURCE so Workbench can parse it.
            marker='\nRECOVERED SOURCE:\n'
            if marker in text:
                text=text.replace(marker,'\n'+extra+marker,1)
            else:
                text += '\n'+extra
            report.write_text(text,encoding='utf-8')
        result['recovered']=physical_files
        return result
