from .base import DetectionResult, RecoveryTechnique
from .pbotools_common import _pbo_tools_marker_scheme, _pbo_tools_banner, _recover_pbo_tools_marked

class PboToolsV18Technique(RecoveryTechnique):
    technique_id = "T002"
    technique_version = "1.0.0"
    family = "pbo-tools"
    name = "pbotools-v18-marker"
    priority = 110
    automatic_threshold = 0.95

    def detect(self, archive):
        pairs=_pbo_tools_marker_scheme(archive,archive.get("entries",[]))
        if not pairs or any(p.get("marker_style") != "numbered-extensionless-run" for p in pairs):
            return DetectionResult(self.name,False,0.0,())
        nums=[p["number"] for p in pairs]
        contiguous=nums==list(range(len(nums)))
        banner=_pbo_tools_banner(archive).lower()
        branded=("pbo tools" in banner or "pbo.tools" in banner)
        repeats=sorted(set(p.get("marker_repeat",0) for p in pairs))
        confidence=1.0 if branded and contiguous else (0.97 if contiguous and len(pairs)>=8 else 0.80)
        return DetectionResult(self.name,True,confidence,(
            f"marker_count={len(pairs)}", f"sequence=0..{nums[-1] if nums else '-'}",
            f"contiguous={contiguous}", f"pbo_tools_banner={branded}",
            "repeat_counts="+",".join(str(x) for x in repeats)))

    def recover(self,pbo_path,out_dir,archive,write_raw=False):
        return _recover_pbo_tools_marked(pbo_path,out_dir,archive,write_raw=write_raw)
