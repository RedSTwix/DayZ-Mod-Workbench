from dataclasses import replace

from .fire_packer import FirePackerTechnique
from .pbotools_v18 import PboToolsV18Technique
from .pbotools_v19 import PboToolsV19Technique
from .generic_include_graph import GenericIncludeGraphTechnique
from .mikero_cyrillic import MikeroCyrillicMangleTechnique
from .kgb_japm import KgbJapmTechnique
from .randomized_cprs import RandomizedCprsIncludeGraphTechnique

TECHNIQUES = (
    FirePackerTechnique(),
    MikeroCyrillicMangleTechnique(),
    KgbJapmTechnique(),
    PboToolsV18Technique(),
    PboToolsV19Technique(),
    RandomizedCprsIncludeGraphTechnique(),
    GenericIncludeGraphTechnique(),
)

def technique_catalog():
    return tuple(sorted((t.metadata() for t in TECHNIQUES), key=lambda item: item["id"]))

def _decorate_detection(technique, result):
    # Detection implementations stay focused on evidence.  The registry owns
    # the stable identity/version contract so metadata cannot drift per branch.
    return replace(
        result,
        technique_id=technique.technique_id,
        technique_version=technique.technique_version,
        family=technique.family,
    )

def select_technique(archive):
    detections=[]
    fallback=None
    for technique in TECHNIQUES:
        result=_decorate_detection(technique, technique.detect(archive))
        detections.append(result)
        if technique.fallback:
            fallback=technique
    candidates=[]
    for technique,result in zip(TECHNIQUES,detections):
        if not technique.fallback and result.matched and result.confidence >= technique.automatic_threshold:
            candidates.append((technique.priority,result.confidence,technique,result))
    candidates.sort(key=lambda x:(x[0],x[1]),reverse=True)
    if candidates:
        _,_,technique,result=candidates[0]
        return technique,result,detections
    if fallback is None:
        raise RuntimeError("No recovery fallback technique registered")
    result=next(r for t,r in zip(TECHNIQUES,detections) if t is fallback)
    return fallback,result,detections
