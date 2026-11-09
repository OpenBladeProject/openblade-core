"""Synthetic-only parser, admission and declarative extension tests."""

import copy
import hashlib
import json
import lzma
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from blade_firmware import FirmwareError
from blade_firmware.core import diff_reports, inspect_bytes, validate_report
from blade_firmware.formats import NSIS_SIGNATURE, decode_intel_hex, extract_nsis_block
from blade_firmware.profiles import load_profiles, read_json, validate_profile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def profile_for(data, **extra):
    return {"schema_version": 1, "id": "synthetic-v1", "label": "Synthetic fixture",
        "model": "TEST-ONLY", "component": "EC", "version": "1", "architecture": "fixture",
        "endianness": "little", "image": {"sha256": digest(data), "size": len(data), "base_address": 0},
        "carriers": [], "annotations": [], "findings": [], **extra}


def hex_record(address=0, kind=0, data=b""):
    payload = bytes([len(data)]) + address.to_bytes(2, "big") + bytes([kind]) + data
    return b":" + (payload + bytes([-sum(payload) & 255])).hex().upper().encode("ascii")


def intel_hex(*records):
    return b"\r\n".join(records) + b"\r\n"


def nsis(payload, compressed=False):
    if compressed:
        filters = [{"id": lzma.FILTER_LZMA1, "dict_size": 1 << 20, "lc": 3, "lp": 0, "pb": 2}]
        payload = b"\x5d" + (1 << 20).to_bytes(4, "little") + lzma.compress(payload, format=lzma.FORMAT_RAW, filters=filters)
    block = struct.pack("<I", len(payload) | (0x80000000 if compressed else 0)) + payload
    return b"MZxx" + struct.pack("<I", 0) + NSIS_SIGNATURE + struct.pack("<II", 8, 28 + len(block) + 4) + block + b"\0" * 4


def bank_fixture():
    data = bytearray(96)
    struct.pack_into("<II", data, 0, 16, 32)
    struct.pack_into("<IIII", data, 16, 1, 0x1000, 2, 0x1001)
    struct.pack_into("<IIII", data, 32, 2, 0x2000, 0, 0x2001)
    mapping = {"pointer_table_offset": 0, "bank_count": 2, "entries_per_bank": 2, "entry_size": 8,
        "metadata_offset": 0, "metadata_size": 4, "address_offset": 4, "address_size": 4,
        "byte_order": "little", "access_values": {"1": "read-only", "2": "read-write"}}
    return bytes(data), profile_for(bytes(data), bank_map=mapping, annotations=[{
        "bank": 1, "index": 0, "address": 0x2000, "width_bits": 8, "name": "Fixture switch",
        "access": "read-write", "evidence_status": "inferred", "bits": [{"bit": 2, "name": "Test bit"}]}])


class IntelHexTests(unittest.TestCase):
    def test_sparse_extended_image_and_start_records(self):
        data = intel_hex(hex_record(kind=4, data=b"\x00\x01"), hex_record(2, data=b"AB"),
            hex_record(6, data=b"C"), hex_record(kind=5, data=b"\0\1\0\2"), hex_record(kind=1))
        result = decode_intel_hex(data)
        self.assertEqual(result.base_address, 65538)
        self.assertEqual(result.data, b"AB\xff\xffC")
        self.assertEqual(result.populated_bytes, 3)

    def test_segment_addressing(self):
        result = decode_intel_hex(intel_hex(hex_record(kind=2, data=b"\x12\x34"), hex_record(2, data=b"X"), hex_record(kind=1)))
        self.assertEqual(result.base_address, 0x12342)

    def test_malformed_incomplete_duplicate_records(self):
        good = hex_record(data=b"A")
        eof = hex_record(kind=1)
        bad = [b"", b"garbage", b":00", good, intel_hex(eof), intel_hex(good, eof, eof),
            intel_hex(good, good, eof), intel_hex(good, hex_record(data=b"B"), eof),
            intel_hex(hex_record(65535, data=b"AB"), eof), intel_hex(hex_record(kind=7), eof),
            intel_hex(good, hex_record(kind=1, data=b"X")), intel_hex(good, hex_record(1, kind=1)),
            intel_hex(hex_record(kind=4, data=b"X"), good, eof), intel_hex(hex_record(1, kind=2, data=b"XX"), good, eof),
            intel_hex(good, hex_record(kind=5, data=b"1234"), hex_record(kind=5, data=b"1234"), eof),
            intel_hex(good[:-2] + b"00", eof), b" \n" + intel_hex(good, eof), intel_hex(good, eof) + b"\n",
            intel_hex(hex_record(data=b""), eof), b":" + b"0" * 1000]
        for data in bad:
            with self.subTest(data=data[:70]), self.assertRaises(FirmwareError):
                decode_intel_hex(data)

    def test_oversized_sparse_span_rejected(self):
        data = intel_hex(hex_record(data=b"A"), hex_record(kind=4, data=b"\x01\x00"), hex_record(data=b"B"), hex_record(kind=1))
        with self.assertRaisesRegex(FirmwareError, "span"):
            decode_intel_hex(data)

    def test_input_size_bound(self):
        with patch("blade_firmware.formats.MAX_INPUT_SIZE", 4), self.assertRaises(FirmwareError):
            decode_intel_hex(intel_hex(hex_record(data=b"A"), hex_record(kind=1)))


class ContainerTests(unittest.TestCase):
    def test_plain_and_compressed_blocks(self):
        for compressed in (False, True):
            with self.subTest(compressed=compressed):
                self.assertEqual(extract_nsis_block(nsis(b"synthetic firmware", compressed), 0), b"synthetic firmware")

    def test_truncated_missing_ambiguous_and_bad_bounds(self):
        good = nsis(b"payload")
        invalid = [good[:12], good[:-1], good + NSIS_SIGNATURE, good[:32] + b"\xff" * 4 + good[36:],
            good[:24] + b"\0" * 4 + good[28:]]
        for data in invalid:
            with self.subTest(data=data[:20]), self.assertRaises(FirmwareError):
                extract_nsis_block(data, 0)
        with self.assertRaises(FirmwareError):
            extract_nsis_block(good, 1)

    def test_compressed_output_and_dictionary_limits(self):
        data = nsis(b"A" * 1000, True)
        with patch("blade_firmware.formats.MAX_IMAGE_SIZE", 32), self.assertRaises(FirmwareError):
            extract_nsis_block(data, 0)
        broken = bytearray(data)
        broken[37:41] = (128 * 1024 * 1024).to_bytes(4, "little")
        with self.assertRaises(FirmwareError):
            extract_nsis_block(bytes(broken), 0)

    def test_truncated_compression_and_trailing_compressed_data(self):
        compressed_payload = nsis(b"A" * 100, True)[36:-4]
        for payload in (compressed_payload[:-2], compressed_payload + b"trailing", b"\xff\0\0\0\0X"):
            carrier = bytearray(nsis(payload))
            struct.pack_into("<I", carrier, 32, len(payload) | 0x80000000)
            with self.subTest(payload=payload[:8]), self.assertRaises(FirmwareError):
                extract_nsis_block(bytes(carrier), 0)

    def test_unknown_carrier_never_extracted(self):
        with self.assertRaisesRegex(FirmwareError, "exact reviewed"):
            inspect_bytes(nsis(b"unreviewed"), "unknown.exe", [])

    def test_exact_carrier_payload_and_image_hash(self):
        payload = b"synthetic firmware"
        carrier = nsis(payload, True)
        profile = profile_for(payload, carriers=[{"sha256": digest(carrier), "size": len(carrier),
            "format": "nsis-blocks-v1", "block_index": 0, "payload_format": "binary", "payload_sha256": digest(payload)}])
        result = inspect_bytes(carrier, "synthetic.exe", [profile])
        self.assertEqual(result.extracted_image, payload)
        self.assertEqual(result.report["profile"]["id"], profile["id"])
        profile["carriers"][0]["payload_sha256"] = "0" * 64
        with self.assertRaisesRegex(FirmwareError, "payload hash"):
            inspect_bytes(carrier, "synthetic.exe", [profile])


class ProfileAndCoreTests(unittest.TestCase):
    def test_colon_leading_unknown_binary_is_metadata_only(self):
        data = b":\x00\xff\x80binary fixture"
        result = inspect_bytes(data, "ec.bin", [])
        self.assertEqual(result.report["source"]["format"], "binary")
        self.assertEqual(result.report["image"]["sha256"], digest(data))
        self.assertEqual(result.report["registers"], [])
        self.assertIsNone(result.extracted_image)

    def test_complete_unlabeled_hex_is_detected_but_explicit_hex_stays_strict(self):
        data = intel_hex(hex_record(data=b"AB"), hex_record(kind=1))
        result = inspect_bytes(data, "unlabeled", [])
        self.assertEqual(result.report["source"]["format"], "intel-hex")
        self.assertEqual(result.extracted_image, b"AB")
        for malformed in (b":\x00\xffbinary", hex_record(data=b"AB")):
            for name in ("bad.hex", "bad.IHEX", "bad.ihx"):
                with self.subTest(name=name), self.assertRaises(FirmwareError):
                    inspect_bytes(malformed, name, [])

    def test_diff_ignores_absent_default_annotation_and_reference_fields(self):
        data = b"synthetic"
        profile = profile_for(data, component="MCU", annotations=[{"address": 0x100, "width_bits": 8,
            "references": [{"kind": "document", "source": "fixture"}], "bits": [{"bit": 1}]}],
            findings=[{"title": "Fixture finding", "references": [{"kind": "document", "source": "fixture"}]}])
        before = inspect_bytes(data, "fixture.bin", [profile]).report
        after = copy.deepcopy(before)
        row = after["registers"][0]
        for field in ("bank", "index", "name", "category", "description", "access", "units", "notes", "evidence_status"):
            del row[field]
        for field in ("address", "label"):
            del row["references"][0][field]
            del after["findings"][0]["references"][0][field]
        for field in ("name", "description", "evidence_status", "references"):
            del row["bits"][0][field]
        snapshot = copy.deepcopy(after)
        difference = diff_reports(before, after)
        self.assertEqual(difference["changes"]["registers"], [])
        self.assertFalse(difference["findings_changed"])
        self.assertEqual(after, snapshot, "Canonical comparison must not mutate caller reports")

    def test_unknown_metadata_only_and_determinism(self):
        first = inspect_bytes(b"unknown", "image.bin", []).report
        self.assertEqual(first, inspect_bytes(b"unknown", "image.bin", []).report)
        self.assertIsNone(first["profile"])
        self.assertEqual(first["registers"], [])
        self.assertEqual(first["thermal_tables"], [])
        self.assertIn("metadata only", first["warnings"][0])
        self.assertEqual(validate_report(first), first)

    def test_explicit_mismatch_and_absent_profile(self):
        for profile_id in ("synthetic-v1", "missing"):
            with self.subTest(profile_id=profile_id), self.assertRaises(FirmwareError):
                inspect_bytes(b"wrong", "image.bin", [profile_for(b"right")], profile_id)

    def test_no_annotation_inheritance_after_one_byte_change(self):
        data, profile = bank_fixture()
        wrong = data[:-1] + b"x"
        report = inspect_bytes(wrong, "modified.bin", [profile]).report
        self.assertEqual(report["registers"], [])
        self.assertEqual(report["findings"], [])

    def test_input_and_image_bounds(self):
        with patch("blade_firmware.core.MAX_INPUT_SIZE", 2), self.assertRaises(FirmwareError):
            inspect_bytes(b"123", "fixture.bin", [])
        with patch("blade_firmware.core.MAX_IMAGE_SIZE", 2), self.assertRaises(FirmwareError):
            inspect_bytes(b"123", "fixture.bin", [])
        with self.assertRaises(FirmwareError):
            inspect_bytes(b"", "fixture.bin", [])

    def test_known_image_base_is_checked_for_hex(self):
        data = b"AB"
        profile = profile_for(data, image={"sha256": digest(data), "size": 2, "base_address": 0x100})
        good_hex = intel_hex(hex_record(0x100, data=data), hex_record(kind=1))
        self.assertEqual(inspect_bytes(good_hex, "fixture.hex", [profile]).report["image"]["base_address"], 0x100)
        with self.assertRaisesRegex(FirmwareError, "base address"):
            inspect_bytes(intel_hex(hex_record(data=data), hex_record(kind=1)), "wrong.hex", [profile])

    def test_ambiguous_same_hash_profiles_rejected(self):
        first = profile_for(b"fixture")
        second = {**first, "id": "second-model"}
        with self.assertRaisesRegex(FirmwareError, "ambiguous"):
            inspect_bytes(b"fixture", "fixture.bin", [first, second])

    def test_bank_map_annotation_separation(self):
        data, profile = bank_fixture()
        report = inspect_bytes(data, "synthetic.bin", [profile]).report
        self.assertEqual(len(report["registers"]), 4)
        self.assertEqual(report["registers"][0]["name"], "Unclassified register")
        self.assertEqual(report["registers"][0]["evidence_status"], "unknown")
        self.assertEqual(report["registers"][2]["key"], "b1:i0")
        self.assertEqual(report["registers"][2]["name"], "Fixture switch")
        self.assertEqual(report["registers"][2]["metadata"], 2)
        self.assertEqual(report["registers"][2]["table_offset"], 32)
        self.assertEqual(report["registers"][2]["bits"][0]["evidence_status"], "unknown")
        validate_report(report)

    def test_mismatched_annotation_address_or_access_rejected(self):
        for field, value in (("address", 1), ("access", "read-only")):
            data, profile = bank_fixture()
            profile["annotations"][0][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(FirmwareError, "annotation"):
                inspect_bytes(data, "fixture.bin", [profile])

    def test_out_of_range_and_overlapping_table_pointers(self):
        for pointer in (16, 0, 95, 0xffffffff):
            data, profile = bank_fixture()
            data = bytearray(data)
            struct.pack_into("<I", data, 4, pointer)
            profile["image"]["sha256"] = digest(data)
            with self.subTest(pointer=pointer), self.assertRaises(FirmwareError):
                inspect_bytes(bytes(data), "fixture.bin", [profile])

    def test_custom_second_model_geometry_without_so6120_semantics(self):
        # Different endian, entry geometry, image base, model and access labels.
        data = bytearray(64)
        data[0:2] = (0x1010).to_bytes(2, "big")
        data[16:19] = b"\x09\x22\x33"
        profile = profile_for(bytes(data), id="second-model", model="SECOND-SYNTHETIC", endianness="big",
            image={"sha256": digest(data), "size": len(data), "base_address": 0x1000},
            bank_map={"pointer_table_offset": 0, "bank_count": 1, "entries_per_bank": 1, "entry_size": 3,
                "metadata_offset": 0, "metadata_size": 1, "address_offset": 1, "address_size": 2,
                "byte_order": "big", "access_values": {"9": "fixture-only"}})
        report = inspect_bytes(bytes(data), "fixture.bin", [profile]).report
        row, = report["registers"]
        self.assertEqual((row["address"], row["access"]), (0x2233, "fixture-only"))
        self.assertEqual(report["thermal_tables"], [])
        self.assertEqual(report["findings"], [])
        self.assertNotIn("SO6120", json.dumps(report))

    def test_annotated_mcu_without_bank_map_raw_and_hex_equivalent(self):
        data = b"\x01\x02\xff\x04"
        profile = profile_for(data, component="MCU", annotations=[{"address": 0x10000100, "width_bits": 32, "name": "Synthetic RAM", "bits": []}])
        hex_data = intel_hex(hex_record(data=data), hex_record(kind=1))
        raw = inspect_bytes(data, "fixture.bin", [profile]).report
        decoded = inspect_bytes(hex_data, "fixture.hex", [profile]).report
        self.assertEqual(raw["image"], decoded["image"])
        self.assertEqual(raw["registers"], decoded["registers"])
        self.assertEqual(raw["registers"][0]["key"], "a268435712")
        self.assertIsNone(raw["registers"][0]["table_offset"])

    def test_profile_validation_rejects_unsupported_duplicate_invalid_data(self):
        data, original = bank_fixture()
        invalid = []
        for key, value in (("schema_version", 2), ("schema_version", True), ("id", "../bad"), ("image", {"sha256": "0" * 64, "size": 0, "base_address": 0})):
            item = copy.deepcopy(original)
            item[key] = value
            invalid.append(item)
        for key, value in (("bank_count", 0), ("address_size", 3), ("metadata_size", 99), ("entry_size", 1), ("byte_order", "native")):
            item = copy.deepcopy(original)
            item["bank_map"][key] = value
            invalid.append(item)
        item = copy.deepcopy(original)
        item["annotations"] *= 2
        invalid.append(item)
        item = copy.deepcopy(original)
        item["annotations"][0]["bits"] = [{"bit": 2}, {"bit": 2}]
        invalid.append(item)
        item = copy.deepcopy(original)
        item["thermal_tables"] = {"decoder": "python:os.system", "group_offsets": [0], "mode_count": 1, "table_size": 512, "mode_names": {}}
        invalid.append(item)
        for item in invalid:
            with self.subTest(profile=item), self.assertRaises(FirmwareError):
                validate_profile(item)

    def test_json_duplicate_keys_constants_and_profile_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "profile.json"
            for content in ('{"x":1,"x":2}', '{"x":NaN}', '{"x":Infinity}', "[" * 1500, "not json"):
                path.write_text(content, encoding="utf-8")
                with self.subTest(content=content[:50]), self.assertRaises(FirmwareError):
                    read_json(path)
            path.write_text(json.dumps(profile_for(b"synthetic")), encoding="utf-8")
            with self.assertRaisesRegex(FirmwareError, "ambiguous"):
                load_profiles([path, path])
            with self.assertRaisesRegex(FirmwareError, "size limit"):
                read_json(path, limit=10)

    def test_external_profile_file_extends_data_only(self):
        data = b"second vendor fixture without SO6120 semantics"
        profile = profile_for(data, id="external-synthetic", model="SECOND-MODEL")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.json"
            path.write_text(json.dumps(profile), encoding="utf-8")
            profiles = load_profiles([path])
            report = inspect_bytes(data, "fixture.bin", profiles, "external-synthetic").report
            self.assertEqual(report["profile"]["model"], "SECOND-MODEL")
            self.assertEqual(report["registers"], [])
            self.assertEqual(report["thermal_tables"], [])
            self.assertNotIn("SO6120", json.dumps(report))

    def test_missing_fields_and_bad_optional_field_types(self):
        profile = profile_for(b"test")
        for key, value in (("annotations", {}), ("findings", 1), ("carriers", "bad")):
            with self.subTest(key=key), self.assertRaises(FirmwareError):
                validate_profile({**profile, key: value})

    def test_diff_preserves_provenance_and_changes(self):
        data, profile = bank_fixture()
        first = inspect_bytes(data, "one.bin", [profile]).report
        second = copy.deepcopy(first)
        second["registers"][2]["name"] = "Changed annotation"
        diff = diff_reports(first, second)
        self.assertTrue(diff["same_profile"])
        self.assertEqual(diff["changes"]["registers"][0]["key"], "b1:i0")
        self.assertEqual(diff["changes"]["thermal_tables"], [])
        self.assertEqual(diff_reports(first, first)["changes"]["registers"], [])


class ThermalAndReportTests(unittest.TestCase):
    def fixture(self):
        image = bytearray(512)
        image[0:5] = bytes([1, 1, 0, 128, 0])  # one stage, fan, sensor; extension at128
        image[8:16] = bytes([40, 1, 60, 1, 2, 3, 4, 5])
        image[128 + 0x21] = 20
        image[128 + 0x26] = 54
        profile = profile_for(bytes(image), thermal_tables={"group_offsets": [0], "mode_count": 1,
            "table_size": 512, "decoder": "so6120-thermal-v1", "mode_names": {"0": "Fixture"}})
        return bytes(image), profile

    def test_thermal_extension_limits_and_stage_scale(self):
        data, profile = self.fixture()
        report = inspect_bytes(data, "fixture.bin", [profile]).report
        table, = report["thermal_tables"]
        self.assertEqual(table["manual_minimum_rpm"], 2000)
        self.assertEqual(table["manual_maximum_rpm"], 5400)
        self.assertEqual(table["stages"][0]["targets_rpm"], [2000])
        self.assertEqual(table["stages"][0]["sensor_bounds"], {"0": [1, 60]})
        validate_report(report)

    def test_bad_extension_stage_and_manual_limit_rejected(self):
        for offset, value in ((0, 255), (3, 8), (4, 255), (0, 0), (128 + 0x21, 100)):
            data, profile = self.fixture()
            data = bytearray(data)
            data[offset] = value
            profile["image"]["sha256"] = digest(data)
            with self.subTest(offset=offset, value=value), self.assertRaises(FirmwareError):
                inspect_bytes(bytes(data), "fixture.bin", [profile])

    def test_full_report_boundary_rejects_malformed_shapes(self):
        data, profile = self.fixture()
        original = inspect_bytes(data, "fixture.bin", [profile]).report
        paths = [(('image', 'size'), True), (('source', 'sha256'), 'bad'),
            (('thermal_tables', 0, 'sensors'), 1), (('thermal_tables', 0, 'sensors'), [0, 0]),
            (('thermal_tables', 0, 'stages'), {}), (('thermal_tables', 0, 'stages', 0, 'targets_rpm'), 'bad'),
            (('thermal_tables', 0, 'stages', 0, 'sensor_bounds'), []),
            (('thermal_tables', 0, 'stages', 0, 'tail'), [1]), (('thermal_tables', 0, 'offset'), 500),
            (('warnings',), [1]), (('findings',), [{'title': 'incomplete'}])]
        for path, value in paths:
            report = copy.deepcopy(original)
            target = report
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            with self.subTest(path=path), self.assertRaises(FirmwareError):
                validate_report(report)
        report = copy.deepcopy(original)
        report["thermal_tables"] *= 2
        with self.assertRaises(FirmwareError):
            diff_reports(original, report)

    def test_register_report_boundary_rejects_duplicate_mismatched_or_incomplete(self):
        data, profile = bank_fixture()
        original = inspect_bytes(data, "fixture.bin", [profile]).report
        for field, value in (("key", "b9:i99"), ("table_offset", 10000), ("metadata", -1), ("bits", "bad")):
            report = copy.deepcopy(original)
            report["registers"][0][field] = value
            with self.subTest(field=field), self.assertRaises(FirmwareError):
                validate_report(report)
        report = copy.deepcopy(original)
        report["registers"] *= 2
        with self.assertRaises(FirmwareError):
            validate_report(report)
        report = copy.deepcopy(original)
        report["profile"] = None
        with self.assertRaisesRegex(FirmwareError, "unknown image"):
            validate_report(report)


if __name__ == "__main__":
    unittest.main()
