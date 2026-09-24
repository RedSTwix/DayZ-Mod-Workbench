import struct
import tempfile
import unittest
from pathlib import Path

from addons.deodol_engine.odol.parser import (
    R,
    _material_extra_render_count,
    _same_lod_semantics,
    parse_odol,
    read_material,
)
from addons.deodol_engine.odol.converter import (
    model_cfg_piece,
    recover_axis_selection,
)
from addons.deodol_engine.pbo.core import (
    _pbo_decode_name,
    pbo_entry_data,
    pbo_parse,
)


def _material_payload(material_version, extra_render_count):
    data = bytearray(b"test_material\0")
    data += struct.pack("<I", material_version)
    data += struct.pack("<" + "f" * 24, *([0.25] * 24))
    if material_version > 10:
        data += struct.pack("<" + "f" * 8, *([0.5] * 8))
    data += struct.pack("<f", 32.0)
    data += struct.pack(
        "<" + "I" * extra_render_count,
        *range(100, 100 + extra_render_count),
    )
    data += struct.pack("<IIII", 1, 2, 3, 4)
    data += b"test_surface.bisurf\0"
    data += struct.pack("<II", 5, 6)
    data += struct.pack("<II", 0, 0)
    # StageTI, present in material v10+.
    data += struct.pack("<I", 7)
    data += b"test_ti.paa\0"
    data += struct.pack("<I", 8)
    data += b"\x01"
    return bytes(data)


class EmbeddedMaterialLayoutTests(unittest.TestCase):
    def _assert_material(self, version, extra_count):
        payload = _material_payload(version, extra_count)
        reader = R(payload, version=53)
        material = read_material(reader, "versioned")
        self.assertEqual(len(payload), reader.pos)
        self.assertEqual(version, material["version"])
        self.assertEqual(extra_count, material["extraRenderParamCount"])
        self.assertEqual(extra_count, len(material["extraRenderParams"]))

    def test_v15_uses_two_parameter_layout(self):
        self._assert_material(15, 2)

    def test_v16_uses_six_parameter_layout(self):
        self._assert_material(16, 6)

    def test_bounded_fallback_policies_remain_available(self):
        self.assertEqual(2, _material_extra_render_count(16, "all-two"))
        self.assertEqual(6, _material_extra_render_count(15, "all-six"))

    def test_equivalent_nan_sentinels_do_not_create_false_ambiguity(self):
        left = {
            "materialLayoutPolicy": "versioned",
            "materials": [{"transform": (float("nan"), 1.0)}],
        }
        right = {
            "materialLayoutPolicy": "all-six",
            "materials": [{"transform": (float("nan"), 1.0)}],
        }
        self.assertTrue(_same_lod_semantics(left, right))


class CorridorCorpusTests(unittest.TestCase):
    def test_local_corridor_corpus_when_available(self):
        pbo = (
            Path.home()
            / "Desktop"
            / "edit mod"
            / "FatalZ ModPack Banov"
            / "PBO"
            / "Corridor_Parts.pbo"
        )
        if not pbo.is_file():
            self.skipTest("Corridor_Parts.pbo corpus is not installed")
        archive = pbo_parse(pbo)
        entries = [
            entry
            for entry in archive["entries"]
            if entry.get("kind") == "file"
            and _pbo_decode_name(entry["name"]).lower().endswith(".p3d")
        ]
        self.assertEqual(15, len(entries))
        parsed = []
        with tempfile.TemporaryDirectory() as folder:
            for index, entry in enumerate(entries):
                model_path = Path(folder) / f"corridor_{index}.p3d"
                model_path.write_bytes(pbo_entry_data(archive, entry))
                parsed.append(parse_odol(model_path))
        self.assertTrue(all(model["version"] == 53 for model in parsed))
        material_versions = {
            material["version"]
            for model in parsed
            for lod in model["lods"]
            for material in lod["materials"]
        }
        self.assertEqual({16}, material_versions)


class BarrelsCorpusTests(unittest.TestCase):
    def test_nan_material_transform_does_not_block_odol_recovery(self):
        pbo = (
            Path.home()
            / "Desktop"
            / "edit mod"
            / "FatalZ ModPack Banov"
            / "PBO"
            / "Barrels_and_casses_ByMeru.pbo"
        )
        if not pbo.is_file():
            self.skipTest("Barrels_and_casses_ByMeru.pbo corpus is not installed")

        archive = pbo_parse(pbo)
        entries = [
            entry
            for entry in archive["entries"]
            if entry.get("kind") == "file"
            and _pbo_decode_name(entry["name"]).lower().endswith(".p3d")
        ]
        self.assertEqual(4, len(entries))
        with tempfile.TemporaryDirectory() as folder:
            models = []
            for index, entry in enumerate(entries):
                model_path = Path(folder) / f"barrels_{index}.p3d"
                model_path.write_bytes(pbo_entry_data(archive, entry))
                models.append(parse_odol(model_path))
        self.assertTrue(all(model["version"] == 53 for model in models))


class AxisEndpointDisambiguationTests(unittest.TestCase):
    @staticmethod
    def _axis_model(second_offset, second_start=2.5, second_end=0.5):
        return {
            "bcenter": (0.0, 0.0, 0.0),
            "lods": [
                {
                    "res": 1.0e15,
                    "verts": [
                        (0.0, -8.0, 0.0),
                        (0.0, 4.0, 0.0),
                        (second_offset, second_start, 0.0),
                        (second_offset, second_end, 0.0),
                    ],
                    "selections": [
                        {"name": "spin_axis", "verts": [0, 1]},
                        {"name": "move_axis", "verts": [2, 3]},
                    ],
                }
            ],
        }

    def test_exact_compiled_endpoint_disambiguates_parallel_axes(self):
        name, sign, _, reason = recover_axis_selection(
            self._axis_model(7.0e-6),
            ((0.0, -8.0, 0.0), (0.0, 1.0, 0.0)),
            "Prop",
            "prop",
            "time",
        )
        self.assertEqual("spin_axis", name)
        self.assertEqual(1.0, sign)
        self.assertIn("exact-endpoint-disambiguation", reason)

    def test_nearly_shared_endpoints_remain_ambiguous(self):
        name, _, _, reason = recover_axis_selection(
            self._axis_model(5.0e-7, -8.0 + 5.0e-7, 4.0 + 5.0e-7),
            ((0.0, -8.0, 0.0), (0.0, 1.0, 0.0)),
            "Prop",
            "prop",
            "time",
        )
        self.assertIsNone(name)
        self.assertIn("ambiguous axis candidates", reason)

    def test_submillimeter_separation_preserves_exact_endpoint_proof(self):
        name, sign, _, reason = recover_axis_selection(
            self._axis_model(1.8e-4, -8.0, 4.0),
            ((0.0, -8.0, 0.0), (0.0, 1.0, 0.0)),
            "qbox_1_rot",
            "colour1",
            "time",
        )
        self.assertEqual("spin_axis", name)
        self.assertEqual(1.0, sign)
        self.assertIn("exact-endpoint-disambiguation", reason)


class BillboardsCorpusTests(unittest.TestCase):
    def test_local_billboards_axes_when_available(self):
        pbo = (
            Path.home()
            / "Desktop"
            / "edit mod"
            / "FatalZ ModPack Banov"
            / "PBO"
            / "FatalZ_Billboards.pbo"
        )
        if not pbo.is_file():
            self.skipTest("FatalZ_Billboards.pbo corpus is not installed")

        archive = pbo_parse(pbo)
        entries = [
            entry
            for entry in archive["entries"]
            if entry.get("kind") == "file"
            and _pbo_decode_name(entry["name"]).lower().endswith(".p3d")
        ]
        self.assertEqual(2, len(entries))

        with tempfile.TemporaryDirectory() as folder:
            for index, entry in enumerate(entries):
                model_path = Path(folder) / f"billboard_{index}.p3d"
                model_path.write_bytes(pbo_entry_data(archive, entry))
                model = parse_odol(model_path)
                body, warnings, recoveries = model_cfg_piece(
                    model, f"billboard_{index}"
                )
                self.assertEqual([], warnings)
                self.assertEqual(2, body.count('axis = "spin1_axis";'))
                self.assertEqual(
                    2,
                    sum(
                        "exact-endpoint-disambiguation" in item
                        for item in recoveries
                    ),
                )


class QBoxCorpusTests(unittest.TestCase):
    def test_local_qbox_axes_when_available(self):
        pbo = (
            Path.home()
            / "Desktop"
            / "edit mod"
            / "FatalZ ModPack Banov"
            / "PBO"
            / "FatalZ_QBox.pbo"
        )
        if not pbo.is_file():
            self.skipTest("FatalZ_QBox.pbo corpus is not installed")

        archive = pbo_parse(pbo)
        entries = [
            entry
            for entry in archive["entries"]
            if entry.get("kind") == "file"
            and _pbo_decode_name(entry["name"]).lower().endswith(".p3d")
        ]
        self.assertEqual(4, len(entries))

        animated_models = 0
        with tempfile.TemporaryDirectory() as folder:
            for index, entry in enumerate(entries):
                model_path = Path(folder) / f"qbox_{index}.p3d"
                model_path.write_bytes(pbo_entry_data(archive, entry))
                model = parse_odol(model_path)
                if not model.get("animations"):
                    continue
                animated_models += 1
                body, warnings, _ = model_cfg_piece(model, f"qbox_{index}")
                self.assertEqual([], warnings)
                self.assertEqual(3, body.count('axis = "colour1_axis";'))
                self.assertEqual(1, body.count('axis = "colour4_axis";'))
        self.assertEqual(2, animated_models)


if __name__ == "__main__":
    unittest.main()
