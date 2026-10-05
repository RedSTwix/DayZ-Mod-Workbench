import struct
import tempfile
import unittest
from pathlib import Path

from addons.deodol_engine.odol.parser import (
    R,
    _material_extra_render_count,
    _parse_v55_animation_header,
    _same_lod_semantics,
    parse_odol,
    read_material,
)
from addons.deodol_engine.odol.converter import (
    convert_lod,
    model_cfg_piece,
    recover_axis_selection,
    recover_model_sections,
    write_mlod,
)
from addons.deodol_engine.pbo.core import (
    _pbo_decode_name,
    pbo_entry_data,
    pbo_parse,
)
from addons.deodol_engine.verifiers.p3d import verify_odol_to_mlod


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


class ProxySelectionRecoveryTests(unittest.TestCase):
    @staticmethod
    def _model(first_faces, first_verts=None):
        selections = [
            {
                "name": "proxy:\\driver.001",
                "verts": list(first_verts or []),
                "faces": list(first_faces),
                "weights": [],
                "sectional": False,
                "sections": [],
            },
            {
                "name": "proxy:\\wheel.001",
                "verts": [],
                "faces": [1],
                "weights": [],
                "sectional": False,
                "sections": [],
            },
        ]
        return {
            "bcenter": (0.0, 0.0, 0.0),
            "mass": 0.0,
            "skeleton": {},
            "lods": [
                {
                    "res": 1.0,
                    "verts": [
                        (0.0, 0.0, 0.0),
                        (1.0, 0.0, 0.0),
                        (0.0, 1.0, 0.0),
                        (2.0, 0.0, 0.0),
                        (3.0, 0.0, 0.0),
                        (2.0, 1.0, 0.0),
                    ],
                    "clips": [0] * 6,
                    "normals": [(0.0, 0.0, 1.0)] * 6,
                    "uvs": [[0.0] * 12],
                    "faces": [[0, 1, 2], [3, 4, 5]],
                    # Both proxies deliberately share one section.
                    "sections": [
                        {"lo": 0, "hi": 16, "tex": -1, "mat": -1, "mat_inline": ""}
                    ],
                    "textures": [],
                    "materials": [],
                    "props": [],
                    "selections": selections,
                    "proxies": [
                        ("driver", None, 1, 0, -1, 0),
                        ("wheel", None, 1, 1, -1, 0),
                    ],
                    "frames": [],
                    "subs": [],
                    "vbr": [],
                }
            ],
        }

    @staticmethod
    def _selected(payload, point_count):
        points = {i for i, value in enumerate(payload[:point_count]) if value}
        faces = {i for i, value in enumerate(payload[point_count:]) if value}
        return points, faces

    def test_shared_section_uses_each_proxys_exact_face(self):
        model = self._model([0])
        points, _, _, tags = convert_lod(model, model["lods"][0])
        tag_map = dict(tags)
        self.assertEqual(
            ({0, 1, 2}, {0}),
            self._selected(tag_map["proxy:\\driver.001"], len(points)),
        )
        self.assertEqual(
            ({3, 4, 5}, {1}),
            self._selected(tag_map["proxy:\\wheel.001"], len(points)),
        )

    def test_shared_section_can_use_unique_vertex_match(self):
        model = self._model([], [0, 1, 2])
        points, _, _, tags = convert_lod(model, model["lods"][0])
        tag_map = dict(tags)
        self.assertEqual(
            ({0, 1, 2}, {0}),
            self._selected(tag_map["proxy:\\driver.001"], len(points)),
        )


class Odol55AnimationHeaderTests(unittest.TestCase):
    @staticmethod
    def _one_rotation_animation():
        data = bytearray(struct.pack("<I", 1))
        data += struct.pack("<I", 0)
        data += b"DrivingWheel\0steeringwheel\0"
        data += struct.pack("<ffffIff", -1.0, 1.0, -1.0, 1.0, 0, 1.0, -1.0)
        data += struct.pack("<i", 0)
        return bytes(data)

    def test_zero_alignment_before_true_animation_flag(self):
        block = self._one_rotation_animation()
        data = b"\0\1" + block
        animations, flag_pos, end_pos = _parse_v55_animation_header(
            data, 0, len(data), 9
        )
        self.assertEqual(1, flag_pos)
        self.assertEqual(len(data), end_pos)
        self.assertEqual(["DrivingWheel"], [c["name"] for c in animations["classes"]])

    def test_zero_alignment_before_false_animation_flag(self):
        animations, flag_pos, end_pos = _parse_v55_animation_header(b"\0\0", 0, 2, 9)
        self.assertIsNone(animations)
        self.assertEqual(1, flag_pos)
        self.assertEqual(2, end_pos)


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


class OlympusCorpusTests(unittest.TestCase):
    def test_vehicle_proxy_triangles_are_preserved_when_available(self):
        preserved_model = (
            Path.home()
            / "AppData"
            / "Local"
            / "Temp"
            / "CodexOlympusProxyFix_20260925"
            / "original"
            / "YRTSK_Wasp_Olympus"
            / "yrtsk_wasp_olympus.p3d"
        )
        pbo = (
            Path.home()
            / "Desktop"
            / "edit mod"
            / "FatalZ ModPack Banov"
            / "PBO"
            / "YRTSK_Wasp_Olympus.pbo"
        )
        if not pbo.is_file() and not preserved_model.is_file():
            self.skipTest("YRTSK_Wasp_Olympus.pbo corpus is not installed")
        with tempfile.TemporaryDirectory() as folder:
            original = Path(folder) / "original.p3d"
            converted = Path(folder) / "converted.p3d"
            if preserved_model.is_file():
                original.write_bytes(preserved_model.read_bytes())
            else:
                archive = pbo_parse(pbo)
                entry = next(
                    item
                    for item in archive["entries"]
                    if item.get("kind") == "file"
                    and _pbo_decode_name(item["name"]).lower()
                    == "yrtsk_wasp_olympus.p3d"
                )
                original.write_bytes(pbo_entry_data(archive, entry))
            model = parse_odol(original)
            write_mlod(model, converted)
            verification = verify_odol_to_mlod(model, converted)

        self.assertEqual(54, model["version"])
        self.assertEqual(
            [10, 10, 10, 10, 10, 0, 0, 9, 10],
            [len(lod.get("proxies") or []) for lod in model["lods"]],
        )
        self.assertEqual("SEMANTIC-EXACT", verification["status"])
        self.assertEqual([], verification["errors"])


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


class ModelSectionRecoveryTests(unittest.TestCase):
    def test_recovers_sectional_selections_in_stable_case_insensitive_order(self):
        model = {
            "version": 54,
            "skeleton": {},
            "animations": {},
            "lods": [
                {
                    "selections": [
                        {"name": "camo", "sectional": True},
                        {"name": "camo2", "sectional": True},
                        {"name": "wheel", "sectional": False},
                        {"name": "proxy:\\driver.001", "sectional": True},
                    ]
                },
                {
                    "selections": [
                        {"name": "CAMO", "sectional": True},
                        {"name": "light_1_1", "sectional": True},
                    ]
                },
            ],
        }

        self.assertEqual(
            ["camo", "camo2", "light_1_1"], recover_model_sections(model)
        )
        body, warnings, recoveries = model_cfg_piece(model, "vehicle")
        self.assertEqual([], warnings)
        self.assertIn(
            'sections[] = {"camo", "camo2", "light_1_1"};', body
        )
        self.assertIn('skeletonName = "";', body)
        self.assertTrue(any("recovered 3 ODOL model section" in x for x in recoveries))


class OlympusModelSectionCorpusTests(unittest.TestCase):
    def test_local_olympus_hidden_texture_sections_when_available(self):
        preserved_model = (
            Path.home()
            / "AppData"
            / "Local"
            / "Temp"
            / "CodexOlympusProxyFix_20260925"
            / "original"
            / "YRTSK_Wasp_Olympus"
            / "yrtsk_wasp_olympus.p3d"
        )
        pbo = (
            Path.home()
            / "Desktop"
            / "edit mod"
            / "FatalZ ModPack Banov"
            / "PBO"
            / "YRTSK_Wasp_Olympus.pbo"
        )
        if not pbo.is_file() and not preserved_model.is_file():
            self.skipTest("YRTSK_Wasp_Olympus.pbo corpus is not installed")

        if preserved_model.is_file():
            model = parse_odol(preserved_model)
        else:
            archive = pbo_parse(pbo)
            entry = next(
                entry
                for entry in archive["entries"]
                if entry.get("kind") == "file"
                and _pbo_decode_name(entry["name"]).lower().endswith(
                    "yrtsk_wasp_olympus.p3d"
                )
            )
            with tempfile.TemporaryDirectory() as folder:
                model_path = Path(folder) / "yrtsk_wasp_olympus.p3d"
                model_path.write_bytes(pbo_entry_data(archive, entry))
                model = parse_odol(model_path)
        sections = recover_model_sections(model)
        if not sections:
            self.skipTest("installed corpus is already a rebuilt ODOL without source sections")
        body, warnings, _ = model_cfg_piece(model, "yrtsk_wasp_olympus")

        self.assertEqual([], warnings)
        for expected in (
            "camo",
            "camo2",
            "light_1_1",
            "light_2_1",
            "light_dashboard",
            "light_brake_1_2",
        ):
            self.assertIn(expected, sections)
            self.assertIn(f'"{expected}"', body)


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
