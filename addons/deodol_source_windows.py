#!/usr/bin/env python3
"""DayZ Mod Workbench modular recovery addon.

Compatibility entrypoint: the Workbench still executes this exact file with
    python deodol_source_windows.py SRC DST [--cfgconvert ...]
Implementation lives in deodol_engine/.

Workbench 1.7.6 compatibility markers (public API, not implementation):
DEBINARIZE_REPORT.txt
PBO_EQUIVALENCE_VERIFICATION.txt
Embedded RVMATs recovered
Forensic RaP RVMATs recovered
Source-proof Embedded RVMATs recovered
ODOL MLOD
--cfgconvert
"""
from pathlib import Path

SCRIPT_VERSION = "UNIVERSAL-AXIS-PBO-v8.2.10-GENERIC-SOURCE-REPLAY-SAFE"
PBO_FULL_PAYLOAD_RECOVERY = True
PBO_TOOLS_MARKER_RECOVERY = True
PBO_TOOLS_V18_MARKER_RECOVERY = True
GENERIC_DECOY_FILTER = True
FIRE_PACKER_MARKER_RECOVERY = True
FIRE_PACKER_SEMANTIC_NAMES = True
FIRE_PACKER_INCLUDE_BRIDGE_RECOVERY = True
MIKERO_CYRILLIC_MANGLE_RECOVERY = True
MIKERO_REFERENCE_REWRITE = True
MIKERO_ROOTED_REFERENCE_RECOVERY = True
MIKERO_FAKE_EXTENSION_BRIDGE_VERIFICATION = True
CLEAN_RAP_RVMAT_RECOVERY = True
CLEAN_RAP_NUMERIC_SUBTYPE_PRESERVATION = True
SKELETON_ONLY_MODEL_CFG_RECOVERY = True
ODOL_UNBOUND_ANIMATION_PRESERVATION = True
ODOL_EXACT_AXIS_LINE_DISAMBIGUATION = True
ODOL_EXACT_AXIS_ENDPOINT_DISAMBIGUATION = True
ODOL_SUBMILLIMETER_AXIS_ENDPOINT_DISAMBIGUATION = True
TECHNIQUE_METADATA_V1 = True
KGB_JAPM_RECOVERY = True
KGB_JAPM_CONDITIONAL_DEOBFUSCATION = True
PBO_COLLISION_SAFE_RECOVERY = True
ODOL53_MATERIAL_V15_ALIGNMENT = True
ODOL_EMBEDDED_MATERIAL_LAYOUT_VARIANTS = True
PBO_PREFLIGHT_ROUTING = True
PBO_GENERIC_REPLAY_VERIFICATION = True
RANDOMIZED_CPRS_INCLUDE_GRAPH_RECOVERY = True
ODOL_NAN_SENTINEL_EQUIVALENCE = True
ODOL_PROXY_FACE_PRESERVATION = True
ODOL55_ANIMATION_FLAG_ALIGNMENT = True
ODOL_MODEL_SECTION_RECOVERY = True

from deodol_engine.runner import main, convert_file as _convert_file, convert_tree as _convert_tree
from deodol_engine.pbo.recovery import recover_obfuscated_pbo
from deodol_engine.pbo.verifier import verify_pbo_archive as _verify_pbo_archive
from deodol_engine.rvmat.recovery import (
    recover_embedded_rvmats as _recover_embedded_rvmats,
    forensic_recover_rvmat_bytes as _forensic_recover_rvmat_bytes,
    _source_proof_embedded_rvmat as _source_proof_impl,
)

def convert_file(src,dst,verify=True):
    return _convert_file(src,dst,verify=verify)

def convert_tree(src_root,dst_root,cfgconvert=None):
    return _convert_tree(src_root,dst_root,cfgconvert=cfgconvert)

def verify_pbo_archive(pbo_path,recovery_dir,cfgconvert=None):
    return _verify_pbo_archive(pbo_path,recovery_dir,cfgconvert=cfgconvert)

def recover_embedded_rvmats(dst_root,parsed_models):
    return _recover_embedded_rvmats(dst_root,parsed_models)

def forensic_recover_rvmat_bytes(damaged):
    return _forensic_recover_rvmat_bytes(damaged)

def _source_proof_embedded_rvmat(damaged,embedded):
    return _source_proof_impl(damaged,embedded)

if __name__ == "__main__":
    print(f"Script: {Path(__file__).resolve()}")
    raise SystemExit(main())
