import tempfile
import unittest
from pathlib import Path

from addons.deodol_engine.pbo.core import PBO_CPRS, pbo_parse
from addons.deodol_engine.pbo.recovery import recover_obfuscated_pbo
from addons.deodol_engine.pbo.verifier import verify_pbo_archive
from addons.deodol_engine.techniques.randomized_cprs import (
    RANDOMIZED_CPRS_MAP_FILE,
    RandomizedCprsIncludeGraphTechnique,
    _randomized_cprs_stats,
)
from addons.deodol_engine.techniques.registry import select_technique


def _entry(name, method=PBO_CPRS, original_size=0):
    return {
        'kind':'file',
        'name':name.encode('latin1'),
        'method':method,
        'orig':original_size,
        'dsz':4 if method==PBO_CPRS and original_size==0 else original_size,
    }


class RandomizedCprsDetectionTests(unittest.TestCase):
    def test_structural_signature_selects_t007(self):
        entries=[
            _entry('config.cpp',original_size=64),
            _entry(r'scripts\4_World\Main.c',original_size=64),
        ]
        entries += [_entry(fr'obf{i}\*.*') for i in range(520)]
        entries += [_entry(fr'safe{i}\empty{i}.paa',method=0) for i in range(12)]
        archive={'entries':entries,'properties':[]}
        technique,detection,_all=select_technique(archive)
        self.assertEqual('T007',technique.technique_id)
        self.assertTrue(detection.matched)
        self.assertGreaterEqual(detection.confidence,0.95)

    def test_ordinary_archive_does_not_match(self):
        archive={'entries':[
            _entry('config.cpp',method=0,original_size=64),
            _entry(r'scripts\4_World\Main.c',method=0,original_size=64),
            _entry(r'textures\optional.paa',method=0),
        ],'properties':[]}
        result=RandomizedCprsIncludeGraphTechnique().detect(archive)
        self.assertFalse(result.matched)

    def test_uncompressed_payload_uses_data_size_not_zero_original_size(self):
        entry=_entry('ordinary.p3d',method=0,original_size=128)
        entry['orig']=0
        stats=_randomized_cprs_stats({'entries':[entry],'properties':[]})
        self.assertEqual(0,stats['empty_entries'])
        self.assertEqual([],stats['safe_empty_binary'])


class RandomizedCprsCorpusTests(unittest.TestCase):
    def test_local_dropship_scripts_when_available(self):
        pbo=(Path.home()/'Desktop'/'edit mod'/'FatalZ ModPack Banov'/'PBO'/
             'YRTSK_Wasp_DropShip_Scripts.pbo')
        if not pbo.is_file():
            self.skipTest('YRTSK_Wasp_DropShip_Scripts.pbo corpus is not installed')

        archive=pbo_parse(pbo)
        technique,detection,_all=select_technique(archive)
        self.assertEqual('T007',technique.technique_id)
        self.assertGreaterEqual(detection.confidence,0.95)

        with tempfile.TemporaryDirectory() as folder:
            out=Path(folder)
            result=recover_obfuscated_pbo(pbo,out)
            self.assertTrue((out/RANDOMIZED_CPRS_MAP_FILE).is_file())
            self.assertEqual(29,result['randomized_cprs_safe_empty_filtered'])
            recovered=list((out/'recovered_source').rglob('*'))
            files=[item for item in recovered if item.is_file()]
            self.assertEqual(14,len(files))
            self.assertFalse(any(item.stat().st_size==0 for item in files))

            verification=verify_pbo_archive(pbo,out)
            self.assertEqual('SEMANTIC-EXACT-INCLUDE-GRAPH',verification['status'])
            report=(out/'PBO_EQUIVALENCE_VERIFICATION.txt').read_text(encoding='utf-8')
            self.assertIn('Randomized Cprs specialized recovery: yes',report)
            self.assertIn('Randomized Cprs empty decoys verified absent: 29/29',report)


if __name__=='__main__':
    unittest.main()
