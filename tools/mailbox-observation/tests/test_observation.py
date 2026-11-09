import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("observation", Path(__file__).parents[1] / "analyze_observation.py")
M = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(M)


def u(value):
    return (value + "\0").encode("utf-16-le")


def obj(opcode, payload, seq=1, pid=7, version=2):
    return dict(type="event", provider=M.OBJECT, id=0, version=version, opcode=opcode,
                pid=pid, time_utc=f"2026-01-01T00:00:{seq:02}Z", payload_hex=payload.hex(),
                event_data={}, render_error=15003, unsupported_payload_form=False)


def fixture():
    held = r"\Device\HarddiskVolume4\Observation\run\held.txt"
    pulse = r"\Device\HarddiskVolume4\Observation\run\pulse.txt"
    events = [obj(37, struct.pack("<HH", 42, 0) + u("File"), 1),
              obj(32, struct.pack("<QIH", 456, 9, 42) + u(""), 2),
              obj(33, struct.pack("<QIH", 456, 9, 42) + u(pulse), 3),
              obj(39, struct.pack("<QIIH", 123, 7, 8, 42) + u(held), 4, pid=0xFFFFFFFF)]
    markers = [dict(kind="held", pid=7, handle="0x8", path=r"C:\Observation\run\held.txt"),
               dict(kind="transient", pid=7, handle="0x9", path=r"C:\Observation\run\pulse.txt")]
    return events, markers


class ObservationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def export(self, events, name="trace.jsonl", etl_hash="a" * 64, etl_bytes=3, complete=True):
        path = self.root / name
        rows = [dict(type="header", schema="openblade.mailbox-trace-export", schema_version=1,
                     etl_sha256=etl_hash, etl_bytes=etl_bytes), *events]
        if complete:
            rows.append(dict(type="end", complete=True, events=len(events)))
        path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        return path

    def test_raw_rundown_pid_and_both_markers(self):
        events, markers = fixture()
        report = M.analyze_export(self.export(events), markers)
        self.assertTrue(all(row["valid"] for row in report["markers"]))
        self.assertEqual(report["file_handles"]["rundown"], dict(total=1, named=1, unnamed=0))
        self.assertEqual(report["coverage"], "not_established")

    def test_wrong_pid_never_validates_marker(self):
        events, markers = fixture()
        markers[0]["pid"] = 99
        self.assertFalse(M.analyze_export(self.export(events), markers)["markers"][0]["valid"])

    def test_transient_target_and_exact_alias_boundary(self):
        events, _ = fixture()
        events[2]["payload_hex"] = (struct.pack("<QIH", 456, 9, 42) + u(r"\Device\InpOutX64")).hex()
        report = M.analyze_export(self.export(events))
        self.assertEqual(report["target_observations"], 1)
        self.assertTrue(report["findings"][0]["paired_create"])
        self.assertFalse(M.target_name(r"\Device\inpoutx64-unrelated"))
        self.assertFalse(M.target_name(r"C:\files\inpoutx64"))
        self.assertTrue(M.target_name(r"\Device\inpoutx64\child"))

    def test_reused_handle_and_duplicate_cannot_validate_transient_control(self):
        for duplicate in (False, True):
            with self.subTest(duplicate=duplicate):
                events, markers = fixture()
                extra = (obj(34, struct.pack("<QIIIHI", 456, 9, 10, 8, 42, 7), 2)
                         if duplicate else obj(32, struct.pack("<QIH", 789, 9, 42) + u(""), 2))
                events.insert(2, extra)
                report = M.analyze_export(self.export(events), markers)
                self.assertFalse(report["markers"][1]["valid"])

    def test_out_of_order_record_breaks_lifetime_join(self):
        events, markers = fixture()
        events.insert(2, obj(37, struct.pack("<HH", 8, 0) + u("Process"), 1))
        report = M.analyze_export(self.export(events), markers)
        self.assertEqual(report["counts"]["out_of_order_events"], 1)
        self.assertFalse(report["markers"][1]["valid"])

    def test_version_three_duplicate_only_taints_object(self):
        event = obj(34, struct.pack("<QIIIHI", 456, 9, 10, 8, 42, 7), version=3)
        self.assertEqual(M.decode_object(event), {"opcode": 34, "object": 456})
        event["version"] = 4
        with self.assertRaises(M.EvidenceError):
            M.decode_object(event)

    def test_unknown_version_retains_coverage_failure(self):
        events, markers = fixture()
        events[-1]["version"] = 3
        report = M.analyze_export(self.export(events), markers)
        self.assertEqual(report["counts"]["unsupported_object_records"], 1)
        self.assertFalse(report["markers"][0]["valid"])

    def test_short_unterminated_and_embedded_nul_names_fail_decode(self):
        for payload in (b"x", struct.pack("<QIIH", 1, 7, 8, 42) + b"a\x00",
                        struct.pack("<QIIH", 1, 7, 8, 42) + u("a\0b")):
            with self.subTest(payload=payload):
                with self.assertRaises(M.EvidenceError):
                    M.decode_object(obj(39, payload))

    def test_truncated_footer_and_extra_records_rejected(self):
        events, _ = fixture()
        with self.assertRaises(M.EvidenceError):
            M.analyze_export(self.export(events, complete=False))
        path = self.export(events)
        with path.open("a", encoding="utf-8") as output:
            output.write(json.dumps(events[0]) + "\n")
        with self.assertRaises(M.EvidenceError):
            M.analyze_export(path)

    def test_duplicate_json_keys_and_nonfinite_values_rejected(self):
        for value in ('{"x":1,"x":2}', '{"x":NaN}'):
            with self.assertRaises(M.EvidenceError):
                M.parse_json(value)

    def test_unnamed_and_unknown_types_stay_counted(self):
        events, _ = fixture()
        events += [obj(39, struct.pack("<QIIH", 998, 9, 12, 42) + u(""), 5),
                   obj(39, struct.pack("<QIIH", 999, 9, 13, 999) + u(""), 6)]
        report = M.analyze_export(self.export(events))
        self.assertEqual(report["file_handles"]["rundown"]["unnamed"], 1)
        self.assertEqual(report["unknown_type_rows"], 1)

    def test_acpi_target_scope_and_unparsed_payload(self):
        event = dict(type="event", provider=M.ACPI, id=7, version=0, opcode=0, pid=7,
                     time_utc="2026-01-01T00:00:01Z", render_error=None,
                     event_data={"AmlMethodName": r"\_SB.PC00.LPCB.EC0.ECRR", "AmlMethodState": "1"})
        report = M.analyze_export(self.export([event]))
        self.assertEqual(report["target_observations"], 1)
        event["unsupported_payload_form"] = True
        report = M.analyze_export(self.export([event]))
        self.assertEqual(report["counts"]["unsupported_acpi_records"], 1)

    def test_bound_line_and_integer_types(self):
        path = self.root / "large.jsonl"
        path.write_bytes(b" " * (M.MAX_LINE + 1))
        with self.assertRaises(M.EvidenceError):
            M.analyze_export(path)
        for value in (True, -1, "1", 1.2):
            with self.assertRaises(M.EvidenceError):
                M.integer(value)

    def test_atomic_report_does_not_overwrite(self):
        path = self.root / "result.json"
        M.write_new_json(path, {"x": 1})
        with self.assertRaises(M.EvidenceError):
            M.write_new_json(path, {"x": 2})
        self.assertEqual(json.loads(path.read_text()), {"x": 1})
        self.assertEqual(list(self.root.glob(".mailbox-analysis-*")), [])

    def manifest(self, loss=0, version=1):
        run_id = "1" * 32
        held, pulse = "held-marker.txt", "calibration-Calibration-transient-marker.txt"
        for name in (held, pulse):
            (self.root / name).write_bytes(b"")
        markers = [dict(kind="held", pid=7, handle="0x8", path=held, open_qpc=0, close_qpc=20000),
                   dict(kind="transient", pid=7, handle="0x9", path=pulse, open_qpc=10500, close_qpc=12500)]
        prefix = "\\Device\\Volume\\" + run_id + "\\"
        events = [obj(37, struct.pack("<HH", 42, 0) + u("File"), 9),
                  obj(32, struct.pack("<QIH", 456, 9, 42) + u(""), 11),
                  obj(33, struct.pack("<QIH", 456, 9, 42) + u(prefix + pulse), 12),
                  obj(39, struct.pack("<QIIH", 123, 7, 8, 42) + u(prefix + held), 16)]
        acpi = [dict(type="event", provider=M.ACPI, id=7, version=0, opcode=0, pid=7,
                     time_utc=f"2026-01-01T00:00:{11 + i:02}Z", render_error=None,
                     event_data={"AmlMethodName": r"\_SB.AC0._PSR", "AmlMethodState": state})
                for i, state in enumerate(("1", "0"))]
        sessions = []
        for kind, rows in (("object", events), ("acpi", acpi)):
            etl = self.root / (kind + ".etl")
            etl.write_bytes(b"synthetic " + kind.encode())
            digest = hashlib.sha256(etl.read_bytes()).hexdigest()
            self.export(rows, kind + ".etl.jsonl", digest, len(etl.read_bytes()))
            stages = {}
            for key, seconds, status in (("before_start", 4, 4201), ("start", 5, 0), ("enable", 6, 0),
                                         ("last_query", 14, 0), ("stop", 17, 0), ("after_stop", 18, 4201)):
                stages[key] = dict(status=status, qpc=seconds * 1000, utc=f"2026-01-01T00:00:{seconds:02}Z",
                                   events_lost=loss, log_buffers_lost=0, realtime_buffers_lost=0)
            sessions.append(dict(kind=kind, file=kind + ".etl", bytes=len(etl.read_bytes()), sha256=digest,
                                 provider_guid="febd7460-3d1d-47eb-af49-c9eeb1e146f2" if kind == "object" else M.ACPI,
                                 keyword=2 if kind == "object" else 1, level=5 if kind == "object" else 4,
                                 file_cap_bytes=M.CAPS_MIB[version][kind] * 1024 * 1024, **stages))
        manifest = dict(schema="openblade.mailbox-observation", schema_version=version, architecture="x64",
                        run_id=run_id, status="captured", failure_reasons=[], qpc_frequency=1000,
                        window_seconds=60, actions=["Calibration"],
                        windows=[dict(kind="calibration", action="Calibration", status="captured", failure_reasons=[],
                                      started_utc="2026-01-01T00:00:10Z", ended_utc="2026-01-01T00:00:15Z",
                                      start_qpc=10000, end_qpc=15000, planned_deadline_qpc=15000, stop_reason="deadline",
                                      markers=markers, manual_marks=[], sessions=sessions)])
        path = self.root / "manifest.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        return path, manifest

    def test_real_relative_markers_work_in_copied_run_and_never_admit(self):
        path, _ = self.manifest()
        report = M.analyze_manifest(path)
        self.assertEqual(report["windows"][0]["collection_integrity"], "controls_valid")
        self.assertEqual(report["assessment"], "requires_review")
        self.assertEqual(report["acpi_target_coverage"], "unresolved")
        self.assertFalse(report["hardware_write_authority"])

    def test_v2_preserves_controls_and_binds_caps_to_manifest_version(self):
        path, manifest = self.manifest(version=2)
        report = M.analyze_manifest(path)
        self.assertEqual(report["collection_integrity"], "controls_valid")
        self.assertFalse(report["hardware_write_authority"])
        manifest["schema_version"] = 1
        path.write_text(json.dumps(manifest))
        report = M.analyze_manifest(path)
        self.assertEqual(report["collection_integrity"], "inconclusive")
        self.assertIn("object:provider_contract_not_confirmed", report["windows"][0]["integrity_issues"])
        self.assertIn("acpi:provider_contract_not_confirmed", report["windows"][0]["integrity_issues"])

    def test_unknown_manifest_version_cannot_inherit_new_caps(self):
        path, manifest = self.manifest()
        for version in (0, 3, True, "2"):
            manifest["schema_version"] = version
            path.write_text(json.dumps(manifest))
            with self.subTest(version=version), self.assertRaises(M.EvidenceError):
                M.analyze_manifest(path)

    def test_export_bounds_refuse_extra_records_and_larger_declared_inputs(self):
        events, _ = fixture()
        path = self.export(events)
        with patch.object(M, "MAX_EVENTS", len(events) - 1), self.assertRaises(M.EvidenceError):
            M.analyze_export(path)
        with patch.object(M, "MAX_EXPORT", path.stat().st_size - 1), self.assertRaises(M.EvidenceError):
            M.analyze_export(path)
        oversized = self.export([], "oversized.jsonl", etl_bytes=M.MAX_ETL + 1)
        with self.assertRaises(M.EvidenceError):
            M.analyze_export(oversized)

    def test_manifest_hash_pins_parsed_bytes_after_path_replacement(self):
        path, _ = self.manifest()
        expected_hash = hashlib.sha256(path.read_bytes()).hexdigest()
        analyze_export = M.analyze_export

        def replace_manifest(*args, **kwargs):
            path.write_bytes(b"replaced after parsing")
            return analyze_export(*args, **kwargs)

        with patch.object(M, "analyze_export", side_effect=replace_manifest):
            report = M.analyze_manifest(path)
        self.assertEqual(report["manifest_sha256"], expected_hash)

    def test_manifest_marker_outside_shared_window_is_invalid(self):
        path, _ = self.manifest()
        export = self.root / "object.etl.jsonl"
        rows = [json.loads(line) for line in export.read_text().splitlines()]
        rows[2]["time_utc"] = "2026-01-01T00:00:09.500Z"
        export.write_text("".join(json.dumps(row) + "\n" for row in rows))
        result = M.analyze_manifest(path)["windows"][0]
        self.assertIn("object:marker_control_failed", result["integrity_issues"])

    def test_marker_close_after_planned_deadline_fails_even_before_actual_end(self):
        path, manifest = self.manifest()
        window = manifest["windows"][0]
        window.update(end_qpc=15020, ended_utc="2026-01-01T00:00:15.020Z")
        window["markers"][1]["close_qpc"] = 15010
        path.write_text(json.dumps(manifest))
        self.assertIn("object:marker_control_failed", M.analyze_manifest(path)["windows"][0]["integrity_issues"])

    def test_declared_success_does_not_hide_failures_or_last_query_loss(self):
        path, manifest = self.manifest()
        manifest["failure_reasons"] = ["incomplete"]
        manifest["windows"][0]["failure_reasons"] = ["early_stop"]
        manifest["windows"][0]["sessions"][0]["last_query"]["events_lost"] = 1
        path.write_text(json.dumps(manifest))
        report = M.analyze_manifest(path)
        self.assertEqual(report["collection_integrity"], "inconclusive")
        self.assertIn("object:last_query_events_lost_not_zero", report["windows"][0]["integrity_issues"])

    def test_missing_failed_session_remains_inconclusive(self):
        path, _ = self.manifest()
        (self.root / "object.etl").unlink()
        report = M.analyze_manifest(path)
        self.assertIn("object:etl_missing", report["windows"][0]["integrity_issues"])

    def test_action_marks_recomputed_against_deadline_and_clock(self):
        window = dict(kind="action", start_qpc=0, end_qpc=65000, planned_deadline_qpc=60000,
                      started_utc="2026-01-01T00:00:00Z", ended_utc="2026-01-01T00:01:05Z", stop_reason="deadline",
                      manual_marks=[dict(kind="departure", source="operator", in_window=True, qpc=10000,
                                         utc="2026-01-01T00:00:10Z"),
                                    dict(kind="return", source="operator", in_window=True, qpc=61000,
                                         utc="2026-01-01T00:01:01Z")])
        _, issues = M.window_timing(window, 1000, 60)
        self.assertIn("operator_mark_not_confirmed", issues)
        self.assertIn("action_settle_period_not_confirmed", issues)

    def test_local_namespace_and_nonregular_inputs_refused(self):
        with self.assertRaises(M.EvidenceError):
            M.local_path(r"\\.\pipe\fake.jsonl")
        with self.assertRaises(M.EvidenceError):
            M.analyze_export(self.root)

    def test_manifest_loss_is_inconclusive(self):
        path, _ = self.manifest(loss=1)
        report = M.analyze_manifest(path)
        self.assertEqual(report["windows"][0]["collection_integrity"], "inconclusive")

    def test_manifest_modified_etl_and_path_escape_rejected(self):
        path, manifest = self.manifest()
        (self.root / "object.etl").write_bytes(b"changed")
        with self.assertRaises(M.EvidenceError):
            M.analyze_manifest(path)
        manifest["windows"][0]["sessions"][0]["file"] = "../other.etl"
        path.write_text(json.dumps(manifest))
        with self.assertRaises(M.EvidenceError):
            M.analyze_manifest(path)


if __name__ == "__main__":
    unittest.main()
