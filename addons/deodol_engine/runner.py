from pathlib import Path
import argparse, shutil, json
from .version import SCRIPT_VERSION
from .odol.parser import parse_odol
from .odol.converter import write_mlod, write_recovered_model_cfg
from .verifiers.p3d import parse_mlod, verify_odol_to_mlod
from .pbo.recovery import recover_obfuscated_pbo
from .pbo.verifier import verify_pbo_archive
from .pbo.probe import probe_pbo
from .rvmat.recovery import recover_forensic_rvmats, recover_embedded_rvmats

def convert_file(src,dst,verify=True):
    src=Path(src); dst=Path(dst); dst.parent.mkdir(parents=True,exist_ok=True)
    h=src.read_bytes()[:4]
    if h==b'MLOD':
        shutil.copy2(src,dst)
        # MLOD input is already editable; validate that it is structurally readable.
        if verify:
            parsed=parse_mlod(dst)
            rp=dst.parent/(dst.name+'.verification.txt')
            rp.write_text(
                'MLOD structural verification\n'
                f'Converter: {SCRIPT_VERSION}\n'
                f'Result: STRUCTURAL-EXACT\n'
                f'LODs: {len(parsed["lods"])}\n',encoding='utf-8')
        return 'MLOD-kept', None
    if h!=b'ODOL':
        raise ValueError(f'Unknown P3D header {h!r}: {src}')
    model=parse_odol(src)
    write_mlod(model,dst)
    if verify:
        rp=dst.parent/(dst.name+'.verification.txt')
        vr=verify_odol_to_mlod(model,dst,rp)
        if vr['status']!='SEMANTIC-EXACT':
            raise ValueError(f'MLOD verification failed: {len(vr["errors"])} semantic differences; see {rp}')
    return f'ODOL{model["version"]}->MLOD', model


def convert_tree(src_root,dst_root,cfgconvert=None):
    src_root=Path(src_root).resolve(); dst_root=Path(dst_root).resolve()
    if src_root==dst_root:
        raise ValueError('Source and destination must be different folders.')
    dst_inside=False
    try:
        dst_root.relative_to(src_root); dst_inside=True
    except ValueError: pass
    dst_root.mkdir(parents=True,exist_ok=True)

    stats={'converted':0,'kept':0,'copied':0,'errors':0,'animated':0,'skeleton_models':0,'pbos':0,'pbo_scripts':0,'rvmats_recovered':0,'rvmats_forensic':0,'rvmats_embedded':0,'rvmats_source_proof':0,'rvmats_semantic_only':0}
    modelcfg_by_folder={}
    parsed_models=[]
    errors=[]

    for p in src_root.rglob('*'):
        if dst_inside:
            try:
                p.resolve().relative_to(dst_root)
                continue
            except ValueError:
                pass
        rel=p.relative_to(src_root); out=dst_root/rel
        if p.is_dir():
            out.mkdir(parents=True,exist_ok=True); continue
        if p.suffix.lower()=='.p3d':
            try:
                status,model=convert_file(p,out)
                print(f'[P3D] {rel} : {status}')
                if status=='MLOD-kept': stats['kept']+=1
                else: stats['converted']+=1
                if model:
                    parsed_models.append((rel,model))
                if model:
                    sk=model.get('skeleton') or {}
                    if sk.get('name'):
                        stats['skeleton_models']+=1
                        modelcfg_by_folder.setdefault(out.parent,[]).append((p.stem,model))
                    anim=model.get('animations') or {}
                    if anim.get('classes'):
                        stats['animated']+=1
            except Exception as e:
                stats['errors']+=1
                errors.append((str(rel),repr(e)))
                print(f'[ERROR] {rel} : {e}')
        elif p.suffix.lower()=='.pbo':
            try:
                # Preserve the original PBO and also emit a clean recovered tree.
                out.parent.mkdir(parents=True,exist_ok=True)
                shutil.copy2(p,out); stats['copied']+=1
                pbo_out=out.parent/(p.stem+'_extracted')
                info=recover_obfuscated_pbo(p,pbo_out,write_raw=False)
                pv=verify_pbo_archive(p,pbo_out,cfgconvert=cfgconvert)
                stats['pbos']+=1; stats['pbo_scripts']+=info['recovered']
                print(f'[PBO] {rel} : recovered={info["recovered"]} cprs_ok={info["cprs_valid"]} errors={info["errors"]} verify={pv["status"]}')
            except Exception as e:
                stats['errors']+=1
                errors.append((str(rel),repr(e)))
                print(f'[ERROR PBO] {rel} : {e}')
        else:
            out.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(p,out); stats['copied']+=1

    warnings=[]
    forensic_result=recover_forensic_rvmats(dst_root)
    stats['rvmats_forensic']=len(forensic_result['recovered'])
    for item in forensic_result['recovered']:
        print(f'[RVMAT FORENSIC] byte-proof recovery from damaged RaP: {item}')
    warnings += forensic_result['warnings']

    rvmat_result=recover_embedded_rvmats(dst_root,parsed_models) if parsed_models else {'recovered':[],'source_proof':[],'semantic_only':[],'warnings':[],'unresolved':[],'total_embedded':0}
    stats['rvmats_embedded']=len(rvmat_result['recovered'])
    stats['rvmats_source_proof']=len(rvmat_result.get('source_proof',[]))
    stats['rvmats_semantic_only']=len(rvmat_result.get('semantic_only',[]))
    stats['rvmats_recovered']=stats['rvmats_forensic']+stats['rvmats_embedded']
    source_proof_set=set(rvmat_result.get('source_proof',[]))
    for item in rvmat_result['recovered']:
        if item in source_proof_set:
            print(f'[RVMAT SOURCE-PROOF] RaP topology + ODOL EmbeddedMaterial, byte-proof: {item}')
        else:
            print(f'[RVMAT SEMANTIC] recovered from direct-layout ODOL EmbeddedMaterial: {item}')
    warnings += rvmat_result['warnings']
    for folder,entries in modelcfg_by_folder.items():
        warnings += write_recovered_model_cfg(folder,entries)

    report=dst_root/'DEBINARIZE_REPORT.txt'
    lines=[
        'Recursive DayZ P3D debinarization report',
        f'Converter: {SCRIPT_VERSION}',
        f'Source: {src_root}', f'Destination: {dst_root}', '',
        f'ODOL converted to MLOD: {stats["converted"]}',
        f'Already-MLOD kept: {stats["kept"]}',
        f'Non-P3D files copied: {stats["copied"]}',
        f'Animated ODOL models detected: {stats["animated"]}',
        f'Skeleton-bearing ODOL models detected: {stats["skeleton_models"]}',
        f'PBO archives recovered: {stats["pbos"]}',
        f'PBO recovered source files: {stats["pbo_scripts"]}',
        f'RVMATs recovered total: {stats["rvmats_recovered"]}',
        f'Forensic RaP RVMATs recovered: {stats["rvmats_forensic"]}',
        f'Embedded RVMATs recovered: {stats["rvmats_embedded"]}',
        f'Source-proof Embedded RVMATs recovered: {stats["rvmats_source_proof"]}',
        f'Semantic-only Embedded RVMATs recovered: {stats["rvmats_semantic_only"]}',
        f'Embedded material references seen: {rvmat_result["total_embedded"]}',
        f'Errors: {stats["errors"]}', ''
    ]
    if warnings:
        lines += ['RECOVERY WARNINGS:'] + [f'- {w}' for w in warnings] + ['']
    if errors:
        lines += ['ERRORS:'] + [f'- {p}: {e}' for p,e in errors] + ['']
    report.write_text('\n'.join(lines),encoding='utf-8')
    return stats,report


def main(argv=None):
    print(f"DayZ P3D Debinarizer {SCRIPT_VERSION}")
    ap=argparse.ArgumentParser(description="DayZ modular source recovery + equivalence verifier")
    ap.add_argument("src", help="Root source folder/file/PBO")
    ap.add_argument("dst", nargs="?", help="Destination folder/file")
    ap.add_argument("--pbo-raw", action="store_true", help="When input is one PBO, also dump every decompressed payload")
    ap.add_argument("--cfgconvert", default=None, help="Path to official CfgConvert.exe; enables config.bin recovery and semantic verification")
    ap.add_argument("--probe-pbo", action="store_true", help="Inspect a PBO and emit a machine-readable preflight decision without extracting it")
    a=ap.parse_args(argv)
    sp=Path(a.src)
    if a.probe_pbo:
        if sp.suffix.lower() != ".pbo":
            ap.error("--probe-pbo requires a .pbo input")
        result=probe_pbo(sp)
        print("PBO_PROBE_JSON " + json.dumps(result, ensure_ascii=True, sort_keys=True))
        return 0
    if not a.dst:
        ap.error("dst is required unless --probe-pbo is used")
    dp=Path(a.dst)
    if sp.is_dir():
        stats,report=convert_tree(sp,dp,cfgconvert=a.cfgconvert)
        print("\nDONE"); print(stats); print("Report:",report)
    elif sp.suffix.lower()==".pbo":
        info=recover_obfuscated_pbo(sp,dp,write_raw=a.pbo_raw)
        pv=verify_pbo_archive(sp,dp,cfgconvert=a.cfgconvert)
        print("\nPBO DONE"); print(info); print("Verification:",pv["status"],pv["report"])
    else:
        status,model=convert_file(sp,dp)
        print(status)
        if model and model.get("animations"):
            write_recovered_model_cfg(dp.parent,[(sp.stem,model)])
    return 0
