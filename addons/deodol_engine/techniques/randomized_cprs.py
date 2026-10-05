from pathlib import Path
import json, re

from .base import DetectionResult, RecoveryTechnique
from .generic_include_graph import recover_generic_include_graph
from ..pbo.core import PBO_CPRS, _pbo_decode_name, _pbo_raw_name_suspicious, _pbo_safe_rel


RANDOMIZED_CPRS_MAP_FILE = 'RANDOMIZED_CPRS_MAP.json'
_MODULE_RE = re.compile(r'(?i)^scripts\\(?:1_core|2_gamelib|3_game|4_world|5_mission)(?:\\|$)')
_BINARY_EXTENSIONS = {'.paa','.pac','.p3d','.ogg','.wav','.wss','.edds','.rtm','.anm','.bin'}


def _entry_name(entry):
    return (entry.get('decoded_name') or _pbo_decode_name(entry.get('name',b''))).replace('/','\\')


def _declared_payload_size(entry):
    """Return the decoded size represented by a PBO header entry.

    For uncompressed entries the PBO header commonly leaves ``orig`` at zero;
    ``dsz`` is the real payload size.  Cprs entries use ``orig`` for the
    decompressed size.  Mixing those meanings made ordinary method-0 files look
    like empty decoys to the T007 detector.
    """
    if entry.get('method')==PBO_CPRS:
        return int(entry.get('orig',0))
    return int(entry.get('dsz',0))


def _randomized_cprs_stats(archive):
    files=[]
    for index,entry in enumerate(archive.get('entries',())):
        if entry.get('kind')!='file':
            continue
        raw=entry.get('name',b'')
        name=_entry_name(entry)
        files.append((index,entry,raw,name))

    count=len(files)
    cprs=sum(1 for _i,e,_r,_n in files if e.get('method')==PBO_CPRS)
    suspicious=sum(1 for _i,_e,r,_n in files if _pbo_raw_name_suspicious(r))
    empty=sum(1 for _i,e,_r,_n in files if _declared_payload_size(e)==0)
    wildcard=sum(1 for _i,_e,_r,n in files if n=='*.*' or n.endswith('\\*.*'))
    config_roots=sum(1 for _i,_e,_r,n in files if n.lower()=='config.cpp')
    module_sources=sum(1 for _i,_e,_r,n in files
                       if _MODULE_RE.match(n) and n.lower().endswith(('.c','.cpp')))
    safe_empty_binary=[]
    for index,entry,raw,name in files:
        if _declared_payload_size(entry)!=0 or _pbo_raw_name_suspicious(raw):
            continue
        if Path(name).suffix.lower() not in _BINARY_EXTENSIONS:
            continue
        safe_empty_binary.append({
            'index':index,
            'decoded_name':name,
            'recovered_path':str(_pbo_safe_rel(name)).replace('/','\\'),
        })

    def ratio(value):
        return (value/count) if count else 0.0

    return {
        'file_entries':count,
        'cprs_entries':cprs,
        'cprs_ratio':ratio(cprs),
        'suspicious_entries':suspicious,
        'suspicious_ratio':ratio(suspicious),
        'empty_entries':empty,
        'empty_ratio':ratio(empty),
        'wildcard_entries':wildcard,
        'wildcard_ratio':ratio(wildcard),
        'config_roots':config_roots,
        'module_sources':module_sources,
        'safe_empty_binary':safe_empty_binary,
    }


def _randomized_cprs_evidence(archive):
    stats=_randomized_cprs_stats(archive)
    matched=(
        stats['file_entries']>=512 and
        stats['cprs_ratio']>=0.80 and
        stats['suspicious_ratio']>=0.75 and
        stats['empty_ratio']>=0.40 and
        stats['wildcard_entries']>=64 and
        stats['wildcard_ratio']>=0.10 and
        len(stats['safe_empty_binary'])>=8 and
        stats['config_roots']>=1 and
        stats['module_sources']>=1
    )
    confidence=0.995 if matched else 0.0
    evidence=(
        f'file_entries={stats["file_entries"]}',
        f'cprs_ratio={stats["cprs_ratio"]:.3f}',
        f'suspicious_name_ratio={stats["suspicious_ratio"]:.3f}',
        f'empty_payload_ratio={stats["empty_ratio"]:.3f}',
        f'wildcard_entries={stats["wildcard_entries"]}',
        f'safe_empty_binary_decoys={len(stats["safe_empty_binary"])}',
        f'config_roots={stats["config_roots"]}',
        f'module_sources={stats["module_sources"]}',
    )
    return matched,confidence,evidence,stats


class RandomizedCprsIncludeGraphTechnique(RecoveryTechnique):
    technique_id='T007'
    technique_version='1.0.0'
    family='randomized-cprs'
    name='randomized-cprs-include-graph'
    # Known branded/marker techniques retain precedence.  This structural
    # detector is deliberately the last specialized technique before T001.
    priority=100
    automatic_threshold=0.95
    fallback=False

    def detect(self,archive):
        matched,confidence,evidence,_stats=_randomized_cprs_evidence(archive)
        return DetectionResult(self.name,matched,confidence,evidence)

    def recover(self,pbo_path,out_dir,archive,write_raw=False):
        out_dir=Path(out_dir)
        matched,confidence,evidence,stats=_randomized_cprs_evidence(archive)
        if not matched:
            raise ValueError('randomized Cprs signature no longer matches during recovery')

        result=recover_generic_include_graph(
            pbo_path,out_dir,archive=archive,write_raw=write_raw,
            suppress_empty_payloads=True)

        mapping={
            'schema':1,
            'technique':'randomized-cprs-include-graph',
            'technique_id':'T007',
            'confidence':confidence,
            'evidence':list(evidence),
            'file_entries':stats['file_entries'],
            'cprs_entries':stats['cprs_entries'],
            'empty_entries':stats['empty_entries'],
            'wildcard_entries':stats['wildcard_entries'],
            'filtered_safe_empty_payloads':stats['safe_empty_binary'],
        }
        (out_dir/RANDOMIZED_CPRS_MAP_FILE).write_text(
            json.dumps(mapping,ensure_ascii=False,indent=2),encoding='utf-8')

        report=out_dir/'PBO_RECOVERY_REPORT.txt'
        text=report.read_text(encoding='utf-8',errors='replace').rstrip()+'\n'
        text += 'Randomized Cprs include-graph recovery: yes\n'
        text += f'Randomized Cprs empty entries classified: {stats["empty_entries"]}\n'
        text += f'Randomized Cprs wildcard entries classified: {stats["wildcard_entries"]}\n'
        text += ('Randomized Cprs safe empty decoys filtered: '
                 f'{len(stats["safe_empty_binary"])}\n')
        report.write_text(text,encoding='utf-8')

        result.update(
            randomized_cprs=True,
            randomized_cprs_empty=stats['empty_entries'],
            randomized_cprs_wildcards=stats['wildcard_entries'],
            randomized_cprs_safe_empty_filtered=len(stats['safe_empty_binary']),
            randomized_cprs_map=out_dir/RANDOMIZED_CPRS_MAP_FILE,
        )
        return result
