"""Offline tests: python -B -m unittest discover -s tools/mailbox-observation/tests.

Synthetic data only. Pin patching is confined to tests; the CLI has no override.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "inspect_acpi_image.py"
SPEC = importlib.util.spec_from_file_location("inspect_acpi_image", SCRIPT)
acpi = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(acpi)


def metadata():
    return json.dumps(dict(acpi.EXPECTED, provider_guid=acpi.PROVIDER_GUID)).encode()


def image():
    b = bytearray(0x600)
    b[:2] = b"MZ"
    struct.pack_into("<I", b, 0x3C, 0x80)
    b[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", b, 0x84, 0x8664, 2, 0, 0, 0, 0xF0, 2)
    opt = 0x98
    struct.pack_into("<H", b, opt, 0x20B)
    struct.pack_into("<I", b, opt + 16, 0x1000)
    struct.pack_into("<Q", b, opt + 24, 0x140000000)
    struct.pack_into("<II", b, opt + 32, 0x1000, 0x200)
    struct.pack_into("<II", b, opt + 56, 0x3000, 0x200)
    struct.pack_into("<I", b, opt + 108, 16)
    struct.pack_into("<II", b, opt + 112 + 8, 0x2080, 40)
    struct.pack_into("<II", b, opt + 112 + 24, 0x2040, 12)
    for at, name, rva, raw, flags in ((0x188, b".text", 0x1000, 0x200, 0x60000020),
                                      (0x1B0, b".rdata", 0x2000, 0x400, 0x40000040)):
        b[at:at + len(name)] = name
        struct.pack_into("<IIII", b, at + 8, 0x200, rva, 0x200, raw)
        struct.pack_into("<I", b, at + 36, flags)
    b[0x400:0x410] = struct.pack("<HBBBBHQ", 7, 0, 16, 4, 0, 105, 0x8000200000000001)
    b[0x420] = 1  # only address validity is inspected, no unwind interpretation
    struct.pack_into("<III", b, 0x440, 0x1000, 0x1010, 0x2020)
    struct.pack_into("<IIIII", b, 0x480, 0x20E0, 0, 0, 0x20C0, 0x2120)
    b[0x4C0:0x4CD] = b"ntoskrnl.exe\0"
    struct.pack_into("<Q", b, 0x4E0, 0x2100)
    b[0x502:0x50B] = b"EtwWrite\0"
    return bytes(b)


class ImageInspectionTests(unittest.TestCase):
    def report(self, data=None):
        data = image() if data is None else data
        with patch.object(acpi, "PINNED_SHA256", hashlib.sha256(data).hexdigest()):
            return acpi.build_report(data, metadata())

    def test_valid_mapping_imports_and_unwind_intervals_without_coverage_claim(self):
        report = self.report()
        hit = report["descriptor_candidates"][0]
        self.assertEqual((hit["file_offset"], hit["rva"], hit["preferred_va"]),
                         (0x400, 0x2000, 0x140002000))
        self.assertEqual(hit["evidence"], "byte-pattern candidate only")
        self.assertEqual(report["imports"][0]["entries"][0]["name"], "EtwWrite")
        self.assertEqual(report["runtime_function_intervals"][0]["end_rva_exclusive"], 0x1010)
        self.assertFalse(report["image"]["runtime_admission"])
        self.assertEqual(report["image"]["signature_validation"], "not performed")
        self.assertEqual(report["coverage"]["nested_evaluation"], "unresolved")
        self.assertEqual(report["coverage"]["external_evaluation"], "unresolved")
        self.assertFalse(report["coverage"]["instruction_references_verified"])

    def test_unknown_hash_refused_before_malformed_pe(self):
        with self.assertRaisesRegex(acpi.InspectionError, "SHA-256"):
            acpi.build_report(b"not a driver", metadata())

    def test_truncations_fail_closed(self):
        for size in (0, 1, 0x3F, 0x84, 0x98, 0x120, 0x1D7, 0x300, 0x5FF):
            with self.subTest(size=size), self.assertRaises(acpi.InspectionError):
                acpi.PEImage(image()[:size])

    def test_architecture_optional_header_and_section_bounds(self):
        mutations = ((0x84, "<H", 0x14C), (0x98, "<H", 0x10B),
                     (0x98 + 16, "<I", 0x3000), (0x98 + 16, "<I", 0x2000),
                     (0x98 + 108, "<I", 17), (0x1B0 + 20, "<I", 0x600),
                     (0x1B0 + 12, "<I", 0x1000), (0x1B0 + 20, "<I", 0x200),
                     (0x1B0 + 8, "<I", 0x2000))
        for offset, fmt, value in mutations:
            b = bytearray(image())
            struct.pack_into(fmt, b, offset, value)
            with self.subTest(offset=offset, value=value), self.assertRaises(acpi.InspectionError):
                acpi.PEImage(bytes(b))

    def test_mapping_rejects_gaps_zero_fill_and_cross_boundary(self):
        b = bytearray(image())
        struct.pack_into("<I", b, 0x1B0 + 8, 0x400)
        pe = acpi.PEImage(bytes(b))
        for rva, count in ((0x800, 1), (0x2200, 1), (0x21FF, 2), (-1, 1), (0x3000, 1), (0, 0)):
            with self.subTest(rva=rva), self.assertRaises(acpi.InspectionError):
                pe.rva_offset(rva, count)
        self.assertEqual(pe.rva_offset(0x21FF), 0x5FF)

    def test_import_and_runtime_directory_corruption(self):
        for at, fmt, value, method in ((0x98 + 120, "<I", 0x2FFF, "imports"),
                                      (0x98 + 124, "<I", 20, "imports"),
                                      (0x480 + 12, "<I", 0x2500, "imports"),
                                      (0x480 + 16, "<I", 0x2FFF, "imports"),
                                      (0x444, "<I", 0x1000, "functions"),
                                      (0x448, "<I", 0x4000, "functions")):
            b = bytearray(image())
            struct.pack_into(fmt, b, at, value)
            with self.subTest(at=at, method=method), self.assertRaises(acpi.InspectionError):
                getattr(acpi.PEImage(bytes(b)), method)()

    def test_complete_metadata_required_no_channel_or_keyword_guess(self):
        for field in acpi.EXPECTED:
            obj = json.loads(metadata())
            del obj[field]
            with self.subTest(field=field), self.assertRaises(acpi.InspectionError):
                acpi.metadata_descriptor(json.dumps(obj).encode())
        obj = json.loads(metadata())
        obj["keywords"] = "0x200000000001"
        with self.assertRaisesRegex(acpi.InspectionError, "differs"):
            acpi.metadata_descriptor(json.dumps(obj).encode())

    def test_saved_provider_text_parsing(self):
        text = f"name: Microsoft-Windows-Kernel-Acpi\nguid: {acpi.PROVIDER_GUID}\nevents:\n  event:\n"
        for name, value in acpi.EXPECTED.items():
            text += f"    {'value' if name == 'event_id' else name}: {value}\n"
        self.assertEqual(acpi.metadata_descriptor(text.encode())["channel"], 16)
        with self.assertRaises(acpi.InspectionError):
            acpi.metadata_descriptor((text + text[text.index('  event:'):]).encode())

    def test_zero_matches_remain_unresolved(self):
        b = bytearray(image())
        b[0x400] = 8
        report = self.report(bytes(b))
        self.assertEqual(report["descriptor_candidates"], [])
        self.assertFalse(report["coverage"]["emitter_identified"])
        self.assertIn("No match does not prove", report["limitations"][1])

    def test_atomic_publication_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            acpi.publish_new(output, {"valid": True})
            original = output.read_bytes()
            with self.assertRaises(acpi.InspectionError):
                acpi.publish_new(output, {"valid": False})
            self.assertEqual(output.read_bytes(), original)
            self.assertEqual([p.name for p in Path(directory).iterdir()], ["report.json"])

    def test_publish_error_leaves_no_partial_target_or_temporary(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            with patch.object(acpi, "publish_exclusive", side_effect=OSError("unsupported filesystem")):
                with self.assertRaises(OSError):
                    acpi.publish_new(output, {"valid": True})
            self.assertFalse(output.exists())
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_publication_race_never_replaces_other_writer(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            original_publish = acpi.publish_exclusive
            def race(source, target):
                Path(target).write_text("other writer")
                return original_publish(source, target)
            with patch.object(acpi, "publish_exclusive", side_effect=race), self.assertRaises(FileExistsError):
                acpi.publish_new(output, {"valid": True})
            self.assertEqual(output.read_text(), "other writer")
            self.assertEqual([p.name for p in Path(directory).iterdir()], ["report.json"])

    def test_cli_bad_input_creates_no_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "input.bin").write_bytes(image())
            (root / "meta.json").write_bytes(metadata())
            result = acpi.main(["--input", str(root / "input.bin"), "--metadata", str(root / "meta.json"),
                                "--output", str(root / "output.json")])
            self.assertEqual(result, 2)
            self.assertFalse((root / "output.json").exists())

    def test_cli_success_preserves_inputs_and_refuses_second_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            data = image()
            (root / "input.bin").write_bytes(data)
            (root / "meta.json").write_bytes(metadata())
            args = ["--input", str(root / "input.bin"), "--metadata", str(root / "meta.json"),
                    "--output", str(root / "output.json")]
            with patch.object(acpi, "PINNED_SHA256", hashlib.sha256(data).hexdigest()):
                self.assertEqual(acpi.main(args), 0)
                original = (root / "output.json").read_bytes()
                self.assertEqual(acpi.main(args), 2)
            self.assertEqual((root / "output.json").read_bytes(), original)
            self.assertEqual((root / "input.bin").read_bytes(), data)
            self.assertEqual((root / "meta.json").read_bytes(), metadata())

    def test_explicit_network_device_paths_and_oversize_files_refused(self):
        for name in (r"\\server\share\input", r"\\.\inpoutx64", r"\\?\C:\data.bin"):
            with self.subTest(name=name), self.assertRaises(acpi.InspectionError):
                acpi.read_regular(Path(name), 100)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "large.bin"
            path.write_bytes(b"12345")
            with self.assertRaises(acpi.InspectionError):
                acpi.read_regular(path, 4)


if __name__ == "__main__":
    unittest.main()
