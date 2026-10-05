from dataclasses import dataclass, field
from typing import Tuple

@dataclass(frozen=True)
class DetectionResult:
    technique: str
    matched: bool
    confidence: float
    evidence: Tuple[str, ...] = field(default_factory=tuple)
    technique_id: str = ""
    technique_version: str = ""
    family: str = ""

class RecoveryTechnique:
    # Stable public identity. IDs never change meaning once assigned.
    technique_id = "T000"
    technique_version = "0.0.0"
    family = "base"
    name = "base"
    priority = 0
    automatic_threshold = 0.95
    fallback = False

    def metadata(self):
        return {
            "id": self.technique_id,
            "name": self.name,
            "version": self.technique_version,
            "family": self.family,
            "priority": self.priority,
            "automatic_threshold": self.automatic_threshold,
            "fallback": self.fallback,
        }

    def detect(self, archive):
        return DetectionResult(self.name, False, 0.0, ())

    def recover(self, pbo_path, out_dir, archive, write_raw=False):
        raise NotImplementedError
