from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess
from .core import pbo_parse, _pbo_decode_name
from ..techniques.registry import select_technique, technique_catalog
from ..rvmat.recovery import recover_clean_rap_rvmats, recover_forensic_rvmats

def recover_obfuscated_pbo(pbo_path,out_dir,write_raw=False):
    pbo_path=Path(pbo_path); out_dir=Path(out_dir); out_dir.mkdir(parents=True,exist_ok=True)
    archive=pbo_parse(pbo_path)
    for entry in archive.get("entries", []):
        if entry.get("kind") == "file":
            entry["decoded_name"] = _pbo_decode_name(entry.get("name", b"")).replace("/", "\\")
    technique,detection,all_detections=select_technique(archive)
    print(f"[PBO TECHNIQUE] {technique.technique_id} {technique.name} v{technique.technique_version} confidence={detection.confidence:.3f}")
    if detection.evidence:
        print("[PBO EVIDENCE] " + "; ".join(detection.evidence))
    selection={
        # Legacy fields retained for Workbench 1.7.7 compatibility.
        "selected": technique.name,
        "selected_confidence": detection.confidence,
        "automatic_threshold": technique.automatic_threshold,
        "fallback": technique.fallback,
        # Stable metadata contract added in engine 8.1.2.
        "catalog_version": 1,
        "selected_id": technique.technique_id,
        "selected_version": technique.technique_version,
        "selected_family": technique.family,
        "selected_detail": {
            **technique.metadata(),
            "confidence": detection.confidence,
            "matched": detection.matched,
            "evidence": list(detection.evidence),
        },
        "evaluated": [
            {
                "technique": d.technique,
                "technique_id": d.technique_id,
                "technique_version": d.technique_version,
                "family": d.family,
                "matched": d.matched,
                "confidence": d.confidence,
                "evidence": list(d.evidence),
            } for d in all_detections
        ],
        "catalog": list(technique_catalog()),
    }
    (out_dir/"PBO_TECHNIQUE_SELECTION.json").write_text(
        json.dumps(selection,ensure_ascii=False,indent=2),encoding="utf-8")
    result=technique.recover(pbo_path,out_dir,archive,write_raw=write_raw)

    # Format recovery is a post-processing layer, independent of the selected
    # obfuscation technique.  A technique decides which payloads are trusted;
    # the RaP recovery layer decides whether a trusted RVMAT can be made editable.
    recovered_root=out_dir/"recovered_source"
    # Clean RaP RVMAT recovery is technique-independent: it only rewrites a
    # recovered .rvmat after the complete RaP stream parses and the rendered
    # text independently re-parses to the same canonical semantic tree.
    # This is safe for generic/plain PBOs as well as specialized obfuscators.
    if recovered_root.exists():
        clean_rvmat=recover_clean_rap_rvmats(recovered_root)
    else:
        clean_rvmat={"recovered":[],"warnings":[],"records":[]}

    # Damaged/forensic RaP remains restricted to the specialized Fire Packer
    # path here; other damaged formats are handled later by ODOL/source-proof
    # reconstruction where stronger evidence may be available.
    if technique.name=="fire-packer-marker" and recovered_root.exists():
        forensic_rvmat=recover_forensic_rvmats(recovered_root)
    else:
        forensic_rvmat={"recovered":[],"warnings":[],"proofs":[]}

    report_path=out_dir/"PBO_RECOVERY_REPORT.txt"
    if report_path.exists():
        report=report_path.read_text(encoding="utf-8",errors="replace").rstrip()+"\n"
        report += f"Standalone clean RaP RVMATs recovered: {len(clean_rvmat['recovered'])}\n"
        report += f"Standalone forensic RaP RVMATs recovered: {len(forensic_rvmat['recovered'])}\n"
        if clean_rvmat['recovered']:
            report += "CLEAN RAP RVMAT RECOVERY:\n"
            for item in clean_rvmat['records']:
                report += f"- {item['path']}: {item['recoveryStatus']}; parsed={item['parsed_bytes']}/{item['original_size']}; sha1={item['original_sha1']}\n"
        for warning in clean_rvmat['warnings'] + forensic_rvmat['warnings']:
            report += f"- RVMAT WARNING: {warning}\n"
        report_path.write_text(report,encoding="utf-8")

    if isinstance(result,dict):
        result.setdefault("technique",technique.name)
        result.setdefault("technique_id",technique.technique_id)
        result.setdefault("technique_version",technique.technique_version)
        result.setdefault("technique_family",technique.family)
        result.setdefault("technique_confidence",detection.confidence)
        result["clean_rap_rvmats"]=len(clean_rvmat["recovered"])
        result["forensic_rap_rvmats"]=len(forensic_rvmat["recovered"])
    return result
