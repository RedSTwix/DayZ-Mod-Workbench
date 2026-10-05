# Central public version/capability contract.
SCRIPT_VERSION = "UNIVERSAL-AXIS-PBO-v8.2.10-GENERIC-SOURCE-REPLAY-SAFE"
ENGINE_API = 1
MODULE_LAYOUT_VERSION = 1
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
TECHNIQUE_CATALOG_VERSION = 1

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

CAPABILITIES = {
    "odol53_55_to_mlod": 1,
    "pbo_full_payload_recovery": 1,
    "pbo_generic_include_graph": 3,
    "pbo_generic_replay_verification": 1,
    "generic_decoy_filter": 1,
    "pbotools_v18_markers": 1,
    "pbotools_v19_markers": 1,
    "fire_packer_markers": 2,
    "fire_packer_semantic_names": 3,
    "fire_packer_include_bridges": 1,
    "mikero_cyrillic_mangle": 1,
    "mikero_reference_rewrite": 2,
    "mikero_rooted_references": 1,
    "mikero_fake_extension_bridge_verification": 1,
    "mikero_ogg_crc_filter": 1,
    "standalone_clean_rap_rvmat": 3,
    "clean_rap_numeric_subtype_preservation": 1,
    "skeleton_only_model_cfg": 1,
    "odol_unbound_animations": 1,
    "odol_exact_axis_line_disambiguation": 1,
    "odol_exact_axis_endpoint_disambiguation": 2,
    "odol_submillimeter_axis_endpoint_disambiguation": 1,
    "technique_metadata": 1,
    "kgb_japm_recovery": 2,
    "kgb_japm_conditional_deobfuscation": 1,
    "pbo_collision_safe_recovery": 1,
    "odol53_material_v15_alignment": 1,
    "odol_embedded_material_layout_variants": 1,
    "pbo_preflight_routing": 1,
    "randomized_cprs_include_graph": 1,
    "odol_nan_sentinel_equivalence": 1,
    "odol_proxy_face_preservation": 1,
    "odol55_animation_flag_alignment": 1,
    "odol_model_section_recovery": 1,
    "rap_forensic": 1,
    "rvmat_source_proof": 1,
}
