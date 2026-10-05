from pathlib import Path
import re

from .core import pbo_parse, _pbo_decode_name
from ..techniques.registry import select_technique

_RESERVED_DEVICE_RE = re.compile(r'^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$', re.I)
_INVALID_WINDOWS_CHARS = set('<>:"|?*')


def _windows_path_hazards(name):
    """Return stable reason codes for a PBO entry path BankRev/Win32 cannot safely materialize.

    This intentionally does not key off mod names or packer brands.  It checks the
    path semantics Windows extraction tools have to obey: illegal characters,
    DOS device names, traversal/rooting, control characters and trailing dot/space.
    """
    if not name:
        return ('empty-name',)

    normalized = name.replace('/', '\\')
    reasons = []
    if normalized.startswith('\\') or re.match(r'^[A-Za-z]:', normalized):
        reasons.append('rooted-path')

    parts = normalized.split('\\')
    for part in parts:
        if part in ('.', '..'):
            reasons.append('path-traversal')
            continue
        if not part:
            continue
        if part[-1:] in (' ', '.'):
            reasons.append('trailing-dot-space')
        if _RESERVED_DEVICE_RE.match(part):
            reasons.append('reserved-device-name')
        if any(ord(ch) < 32 for ch in part):
            reasons.append('control-character')
        if any(ch in _INVALID_WINDOWS_CHARS for ch in part):
            reasons.append('illegal-windows-character')

    # Stable ordering + dedupe.
    return tuple(dict.fromkeys(reasons))


def probe_pbo(path):
    """Classify whether Workbench should bypass BankRev extraction.

    The decision is generic:
      * any registered specialized recovery technique selected at/above its own
        automatic threshold; or
      * any header entry whose path cannot be safely represented by Win32.

    Unknown/future protectors therefore still bypass BankRev when their header
    creates unsafe Windows paths, without hardcoding a mod or technique name.
    """
    path = Path(path)
    archive = pbo_parse(path)
    technique, detection, detections = select_technique(archive)

    unsafe_entries = []
    hazard_counts = {}
    file_entries = 0
    for entry in archive.get('entries', []):
        if entry.get('kind') != 'file':
            continue
        file_entries += 1
        name = _pbo_decode_name(entry.get('name', b''))
        hazards = _windows_path_hazards(name)
        if not hazards:
            continue
        unsafe_entries.append((name, hazards))
        for code in hazards:
            hazard_counts[code] = hazard_counts.get(code, 0) + 1

    specialized = (not technique.fallback and detection.matched and
                   detection.confidence >= technique.automatic_threshold)
    unsafe = bool(unsafe_entries)
    prefer_python = specialized or unsafe

    reason_codes = []
    if specialized:
        reason_codes.append('specialized-technique')
    if unsafe:
        reason_codes.append('windows-unsafe-header-paths')

    return {
        'schema': 1,
        'pbo': path.name,
        'prefer_python': prefer_python,
        'reason_codes': reason_codes,
        'technique': detection.technique,
        'technique_id': detection.technique_id,
        'technique_version': detection.technique_version,
        'technique_family': detection.family,
        'technique_matched': bool(detection.matched),
        'technique_confidence': float(detection.confidence),
        'automatic_threshold': float(technique.automatic_threshold),
        'technique_fallback': bool(technique.fallback),
        'file_entries': file_entries,
        'unsafe_path_entries': len(unsafe_entries),
        'hazard_counts': hazard_counts,
        'unsafe_examples': [
            {'name': name, 'reasons': list(reasons)}
            for name, reasons in unsafe_entries[:8]
        ],
        'evaluated': [
            {
                'technique': item.technique,
                'technique_id': item.technique_id,
                'matched': bool(item.matched),
                'confidence': float(item.confidence),
                'evidence': list(item.evidence),
            }
            for item in detections
        ],
    }
