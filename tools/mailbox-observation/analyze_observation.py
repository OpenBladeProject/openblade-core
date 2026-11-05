"""Bounded, offline-only inspection of recorder manifests and ETL JSONL exports.

No report from this module authorizes writes or certifies ACPI tracing coverage.
Raw x64 version-2 layouts are checked against marker records before attribution.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timedelta
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
import tempfile

OBJECT = "89497f50-effe-4440-8cf2-ce6b1cdcaca7"
ACPI = "c514638f-7723-485b-bcfc-96565d735d4a"
MAX_EVENTS = 4_000_000
MAX_EXPORT = 1536 * 1024 * 1024
MAX_ETL = 256 * 1024 * 1024
CAPS_MIB = {1: {"object": 128, "acpi": 16}, 2: {"object": 256, "acpi": 32}}
MAX_LINE = 1024 * 1024
MAX_OPEN = 300_000
ALIASES = ("\\device\\inpoutx64", "\\??\\inpoutx64", "\\\\.\\inpoutx64",
           "\\global??\\inpoutx64", "\\dosdevices\\inpoutx64")
TARGET_METHODS = {"TBCC", "ECRR", "BMRD", "BMWD"}


class EvidenceError(ValueError):
    pass


def local_path(path):
    path = Path(path)
    if str(path).replace("/", "\\").startswith("\\\\"):
        raise EvidenceError("UNC and device namespace paths are not supported")
    return path


@contextmanager
def regular_stream(path, limit):
    path = local_path(path)
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode) or getattr(before, "st_file_attributes", 0) & 0x400:
        raise EvidenceError("Input must be an ordinary file, not a link/device")
    with path.open("rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > limit:
            raise EvidenceError("Input is not a bounded regular file")
        yield source
        after = os.fstat(source.fileno())
        if after.st_size != info.st_size or after.st_mtime_ns != info.st_mtime_ns:
            raise EvidenceError("Input changed during inspection")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise EvidenceError("Duplicate JSON key")
        result[key] = value
    return result


def parse_json(text):
    def invalid_constant(value):
        raise EvidenceError("Non-finite JSON value")
    return json.loads(text, object_pairs_hook=unique_object, parse_constant=invalid_constant)


def integer(value, maximum=(1 << 64) - 1):
    if type(value) is not int or value < 0 or value > maximum:
        raise EvidenceError("Invalid unsigned integer")
    return value


def time_value(value):
    if not isinstance(value, str) or len(value) > 64:
        raise EvidenceError("Invalid timestamp")
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.utcoffset() is None:
        raise EvidenceError("Timestamp has no offset")
    return result


def sha256(path, limit=128 * 1024 * 1024):
    with regular_stream(path, limit) as source:
        result = hashlib.sha256()
        size = 0
        for block in iter(lambda: source.read(1024 * 1024), b""):
            size += len(block)
            if size > limit:
                raise EvidenceError("Input grew beyond hash bound")
            result.update(block)
    return result.hexdigest()


def write_new_json(path, report):
    path = local_path(path)
    if path.exists() or path.is_symlink():
        raise EvidenceError("Output already exists")
    # Windows rename refuses an existing destination; POSIX link is exclusive.
    fd, temporary = tempfile.mkstemp(prefix=".mailbox-analysis-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as output:
            json.dump(report, output, indent=2, ensure_ascii=True, allow_nan=False)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        if os.name == "nt":
            os.rename(temporary, path)
        else:
            os.link(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def unicode_tail(payload, offset):
    tail = payload[offset:]
    if len(tail) < 2 or len(tail) % 2 or len(tail) > 65536:
        raise EvidenceError("Invalid UTF-16 payload length")
    value = tail.decode("utf-16-le", errors="strict")
    if not value.endswith("\0") or "\0" in value[:-1]:
        raise EvidenceError("Unterminated or embedded-NUL name")
    return value[:-1]


def decode_object(event):
    opcode = event.get("opcode")
    # Version-3 duplicate records share only the independently checked object prefix.
    # They taint joins; no PID, handle or name attribution is derived from them.
    versions = (2, 3) if opcode == 34 else (2,)
    if event.get("version") not in versions or event.get("unsupported_payload_form"):
        raise EvidenceError("Unsupported Object event layout")
    raw = event.get("payload_hex")
    if not isinstance(raw, str) or len(raw) > 131072 or len(raw) % 2 or not re.fullmatch(r"[0-9a-fA-F]*", raw):
        raise EvidenceError("Missing or invalid raw Object payload")
    payload = bytes.fromhex(raw)
    if opcode == 37:
        if len(payload) < 6:
            raise EvidenceError("Short object-type rundown")
        return {"opcode": opcode, "type_id": struct.unpack_from("<H", payload)[0],
                "name": unicode_tail(payload, 4)}
    if opcode == 39:
        if len(payload) < 20:
            raise EvidenceError("Short handle rundown")
        obj, pid, handle, kind = struct.unpack_from("<QIIH", payload)
        return dict(opcode=opcode, object=obj, pid=pid, handle=handle, type_id=kind,
                    name=unicode_tail(payload, 18))
    if opcode in (32, 33):
        if len(payload) < 14:
            raise EvidenceError("Short live handle record")
        obj, handle, kind = struct.unpack_from("<QIH", payload)
        return dict(opcode=opcode, object=obj, pid=integer(event.get("pid"), 0xFFFFFFFF),
                    handle=handle, type_id=kind,
                    name="" if len(payload) == 14 else unicode_tail(payload, 14))
    if opcode == 34:
        if len(payload) < 26:
            raise EvidenceError("Short duplicate record")
        # Only object identity is used: duplicate PID fields are not independently validated.
        return {"opcode": opcode, "object": struct.unpack_from("<Q", payload)[0]}
    raise EvidenceError("Unsupported Object opcode")


def target_name(name):
    normalized = name.casefold()
    return any(normalized == alias or normalized.startswith(alias + "\\") for alias in ALIASES)


def marker_match(row, marker):
    suffix = marker.get("_expected_suffix")
    if suffix is None:
        # Legacy inspection API only: current manifests use UUID-bound relative markers.
        path = marker.get("path")
        if not isinstance(path, str) or not re.match(r"^[A-Za-z]:\\", path) or "\0" in path:
            raise EvidenceError("Marker path must be a Windows absolute file path")
        suffix = path[2:]
    suffix = suffix.casefold()
    handle = marker.get("handle")
    if not isinstance(handle, str) or not re.fullmatch(r"0x[0-9a-fA-F]{1,16}", handle):
        raise EvidenceError("Invalid marker handle")
    return (row["pid"] == integer(marker.get("pid"), 0xFFFFFFFF)
            and row["handle"] == int(handle, 16)
            and row["name"].casefold().endswith(suffix))


def analyze_export(path, markers=()):
    """Return observations, never an absent-client or automatic admission verdict."""
    path = local_path(path)
    counts = Counter()
    types = {}
    per_type = Counter()
    named_per_type = Counter()
    methods = Counter()
    findings = []
    target_count = 0
    marker_hits = [Counter() for _ in markers]
    marker_types = [set() for _ in markers]
    active = {}
    tainted_objects = set()
    previous_time = None
    header = footer = None
    seen = 0
    with regular_stream(path, MAX_EXPORT) as source:
        consumed = 0
        while line := source.readline(MAX_LINE + 1):
            consumed += len(line)
            if consumed > MAX_EXPORT:
                raise EvidenceError("Export grew beyond size bound")
            if len(line) > MAX_LINE:
                raise EvidenceError("JSONL record exceeds size bound")
            record = parse_json(line.decode("utf-8"))
            if not isinstance(record, dict):
                raise EvidenceError("JSONL record must be an object")
            if header is None:
                if (record.get("type"), record.get("schema"), record.get("schema_version")) != (
                        "header", "openblade.mailbox-trace-export", 1):
                    raise EvidenceError("Unsupported export header")
                if not re.fullmatch(r"[0-9a-f]{64}", record.get("etl_sha256", "")):
                    raise EvidenceError("Invalid bound ETL hash")
                integer(record.get("schema_version"), 1)
                integer(record.get("etl_bytes"), MAX_ETL)
                header = record
                continue
            if footer is not None:
                raise EvidenceError("Records follow export footer")
            if record.get("type") == "end":
                footer = record
                continue
            if record.get("type") != "event" or seen >= MAX_EVENTS:
                raise EvidenceError("Invalid event record or event bound reached")
            seen += 1
            provider = record.get("provider")
            timestamp = time_value(record.get("time_utc"))
            if previous_time is not None and timestamp < previous_time:
                counts["out_of_order_events"] += 1
                active.clear()  # Preserve counts, refuse lifetime joins across ordering ambiguity.
            previous_time = timestamp
            if record.get("unsupported_payload_form"):
                counts["unsupported_payload_forms"] += 1
            if provider == OBJECT:
                counts["object_events"] += 1
                try:
                    row = decode_object(record)
                except (ValueError, TypeError, struct.error):
                    counts["unsupported_object_records"] += 1
                    active.clear()
                    continue
                opcode = row["opcode"]
                counts[f"opcode_{opcode}"] += 1
                if opcode == 37:
                    kind, name = row["type_id"], row["name"]
                    if kind in types and types[kind] != name:
                        counts["conflicting_type_names"] += 1
                    else:
                        types[kind] = name
                    continue
                if opcode == 34:
                    tainted_objects.add(row["object"])
                    if len(tainted_objects) > MAX_OPEN:
                        raise EvidenceError("Duplicate-state bound reached")
                    continue
                per_type[(opcode, row["type_id"])] += 1
                if row["name"]:
                    named_per_type[(opcode, row["type_id"])] += 1
                key = (row["pid"], row["handle"])
                paired = False
                if opcode == 32:
                    if key in active:
                        counts["ambiguous_reused_handles"] += 1
                        tainted_objects.add(active[key][0])
                        tainted_objects.add(row["object"])
                    active[key] = (row["object"], row["type_id"], timestamp)
                    if len(active) > MAX_OPEN or len(tainted_objects) > MAX_OPEN:
                        raise EvidenceError("Live-handle state bound reached")
                elif opcode == 33:
                    opened = active.pop(key, None)
                    paired = (opened is not None and opened[:2] == (row["object"], row["type_id"])
                              and row["object"] != 0 and row["object"] not in tainted_objects
                              and opened[2] < timestamp and row["pid"] != 0xFFFFFFFF)
                    counts["paired_closes" if paired else "unpaired_closes"] += 1
                if row["name"] and target_name(row["name"]):
                    target_count += 1
                    if len(findings) < 100:
                        findings.append({"kind": "named_handle", "opcode": opcode, "pid": row["pid"],
                                         "handle": hex(row["handle"]), "type_id": row["type_id"],
                                         "paired_create": paired, "time_utc": record["time_utc"], "name": row["name"]})
                for index, marker in enumerate(markers):
                    if row["name"] and marker_match(row, marker):
                        marker_types[index].add(row["type_id"])
                        interval = marker.get("_time_bounds")
                        if interval is not None and (not interval[0] <= timestamp <= interval[1]
                                or (opcode == 33 and paired and not interval[0] <= opened[2] <= interval[1])):
                            marker_hits[index]["outside_window"] += 1
                            continue
                        if opcode == 39:
                            marker_hits[index]["rundown"] += 1
                        if opcode == 33:
                            marker_hits[index]["close"] += 1
                            if paired:
                                marker_hits[index]["paired_close"] += 1
            elif provider == ACPI:
                counts["acpi_events"] += 1
                data = record.get("event_data")
                if (record.get("id") != 7 or record.get("version") != 0 or not isinstance(data, dict)
                        or record.get("unsupported_payload_form") or record.get("render_error") is not None):
                    counts["unsupported_acpi_records"] += 1
                    continue
                name, state = data.get("AmlMethodName"), data.get("AmlMethodState")
                if not isinstance(name, str) or len(name) > 4096 or "\0" in name or state not in ("0", "1"):
                    counts["unsupported_acpi_records"] += 1
                    continue
                methods[name] += 1
                if len(methods) > 4096:
                    raise EvidenceError("Method-name cardinality bound reached")
                counts["method_start" if state == "1" else "method_finish"] += 1
                if name.rsplit(".", 1)[-1].lstrip("\\").upper() in TARGET_METHODS:
                    target_count += 1
                    if len(findings) < 100:
                        findings.append({"kind": "acpi_method", "name": name, "state": state,
                                         "time_utc": record["time_utc"]})
    if (header is None or footer is None or footer.get("complete") is not True
            or type(footer.get("events")) is not int or footer["events"] != seen):
        raise EvidenceError("Incomplete export or event count mismatch")
    file_types = {kind for kind, name in types.items() if name == "File"}
    if len(file_types) != 1 and counts["object_events"]:
        counts["missing_or_ambiguous_file_type"] += 1
    file_counts = {}
    for opcode, label in ((39, "rundown"), (32, "create"), (33, "close")):
        total = sum(per_type[(opcode, kind)] for kind in file_types)
        named = sum(named_per_type[(opcode, kind)] for kind in file_types)
        file_counts[label] = {"total": total, "named": named, "unnamed": total - named}
    unknown_type_rows = sum(value for (_, kind), value in per_type.items() if kind not in types)
    validations = []
    for index, marker in enumerate(markers):
        hits = marker_hits[index]
        valid_type = len(file_types) == 1 and marker_types[index] == file_types
        valid = valid_type and marker.get("_timing_valid", True) and hits["outside_window"] == 0 and ((marker.get("kind") == "held" and hits["rundown"] == 1)
                               or (marker.get("kind") == "transient" and hits["close"] == 1
                                   and hits["paired_close"] == 1 and hits["rundown"] == 0))
        validations.append({"kind": marker.get("kind"), "valid": valid, "matches": dict(hits)})
    return {"etl_sha256": header["etl_sha256"], "etl_bytes": header["etl_bytes"],
            "events": seen, "counts": dict(counts), "file_handles": file_counts,
            "unknown_type_rows": unknown_type_rows, "methods": dict(sorted(methods.items())),
            "markers": validations, "target_observations": target_count, "findings": findings,
            "findings_truncated": target_count > len(findings),
            "observation": "target_observed" if target_count else "no_target_observed",
            "coverage": "not_established", "unclosed_created_handles": len(active)}


def contained_file(directory, name):
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,200}", name):
        raise EvidenceError("Expected one relative artifact filename")
    unresolved = directory / name
    if unresolved.is_symlink() or getattr(unresolved.lstat(), "st_file_attributes", 0) & 0x400:
        raise EvidenceError("Artifact may not be a link or reparse point")
    result = unresolved.resolve()
    if not result.is_relative_to(directory.resolve()) or not result.is_file():
        raise EvidenceError("Artifact is missing or escapes run directory")
    return result


def window_timing(window, frequency, duration):
    """Independently check QPC and wall-clock interval; the declared status is not evidence."""
    problems = []
    try:
        start, end = integer(window.get("start_qpc")), integer(window.get("end_qpc"))
        utc_start, utc_end = time_value(window.get("started_utc")), time_value(window.get("ended_utc"))
        if end < start or abs((utc_end - utc_start).total_seconds() - (end - start) / frequency) > 0.25:
            raise EvidenceError("Inconsistent window clocks")
        if window.get("kind") == "baseline":
            deadline = end
            if window.get("planned_deadline_qpc") is not None or window.get("stop_reason") != "snapshot":
                problems.append("baseline_timing_not_confirmed")
        else:
            deadline = integer(window.get("planned_deadline_qpc"))
            maximum = 5 if window.get("kind") == "calibration" else duration
            if not start < deadline <= start + maximum * frequency or end < deadline:
                problems.append("deadline_not_confirmed")
            if window.get("stop_reason") != "deadline":
                problems.append("unexpected_stop_reason")
        shared_end = min(end, deadline)
        shared_utc_end = min(utc_end, utc_start + timedelta(seconds=(shared_end - start) / frequency))
        if window.get("kind") == "action":
            marks = window.get("manual_marks")
            if not isinstance(marks, list) or len(marks) != 2:
                problems.append("operator_marks_incomplete")
            else:
                positions = []
                for mark, expected in zip(marks, ("departure", "return")):
                    qpc = integer(mark.get("qpc"))
                    utc = time_value(mark.get("utc"))
                    if (mark.get("kind") != expected or mark.get("source") != "operator"
                            or mark.get("in_window") is not True or not start <= qpc <= shared_end
                            or abs((utc - utc_start).total_seconds() - (qpc - start) / frequency) > 0.25):
                        problems.append("operator_mark_not_confirmed")
                    positions.append(qpc)
                if (positions[0] - start < 10 * frequency or positions[1] - positions[0] < 15 * frequency
                        or shared_end - positions[1] < 15 * frequency):
                    problems.append("action_settle_period_not_confirmed")
        return {"start_qpc": start, "end_qpc": shared_end, "start_utc": utc_start,
                "end_utc": shared_utc_end}, problems
    except (ValueError, TypeError, OverflowError):
        return None, ["window_timing_unavailable"]


def prepare_markers(directory, run_id, window, session, timing):
    raw_markers = window.get("markers")
    if not isinstance(raw_markers, list) or len(raw_markers) > 2:
        raise EvidenceError("Invalid marker list")
    markers = []
    for original in raw_markers:
        marker = dict(original)
        name = marker.get("path")
        expected_name = ("held-marker.txt" if marker.get("kind") == "held" else
                         f"{window['kind']}-{window['action']}-transient-marker.txt")
        if marker.get("kind") not in ("held", "transient") or name != expected_name:
            raise EvidenceError("Unexpected marker filename")
        path = contained_file(directory, name)
        with regular_stream(path, 4096):
            pass
        marker["_expected_suffix"] = "\\" + run_id + "\\" + name
        marker["_timing_valid"] = False
        try:
            opened, closed = integer(marker.get("open_qpc")), integer(marker.get("close_qpc"))
            if timing is None or closed < opened:
                raise EvidenceError("Marker lifetime unavailable")
            if marker["kind"] == "transient":
                marker["_timing_valid"] = timing["start_qpc"] <= opened <= closed <= timing["end_qpc"]
                marker["_time_bounds"] = (timing["start_utc"], timing["end_utc"])
            else:
                start, stop = session.get("start") or {}, session.get("stop") or {}
                start_qpc, stop_qpc = integer(start.get("qpc")), integer(stop.get("qpc"))
                marker["_timing_valid"] = opened <= start_qpc <= stop_qpc <= closed
                marker["_time_bounds"] = (time_value(start.get("utc")), time_value(stop.get("utc")))
        except (ValueError, TypeError):
            pass
        markers.append(marker)
    return markers


def analyze_manifest(path):
    path = local_path(path)
    with regular_stream(path, 4 * 1024 * 1024) as source:
        text = source.read(4 * 1024 * 1024 + 1)
    if len(text) > 4 * 1024 * 1024:
        raise EvidenceError("Manifest grew beyond bound")
    manifest = parse_json(text.decode("utf-8"))
    if (manifest.get("schema"), manifest.get("architecture")) != ("openblade.mailbox-observation", "x64"):
        raise EvidenceError("Unsupported recorder manifest")
    version = integer(manifest.get("schema_version"), max(CAPS_MIB))
    if version not in CAPS_MIB:
        raise EvidenceError("Unsupported recorder manifest version")
    frequency = integer(manifest.get("qpc_frequency"), 10_000_000_000)
    if frequency == 0:
        raise EvidenceError("Invalid QPC frequency")
    duration = integer(manifest.get("window_seconds"), 60)
    if duration < 50:
        raise EvidenceError("Unsupported action duration")
    run_id = manifest.get("run_id")
    if not isinstance(run_id, str) or not re.fullmatch(r"[0-9a-f]{32}", run_id):
        raise EvidenceError("Invalid run identity")
    actions = manifest.get("actions")
    if (not isinstance(actions, list) or not 1 <= len(actions) <= 4 or actions[0] != "Calibration"
            or not all(a in ("Calibration", "TypeC", "AcPower", "ThermalProfile") for a in actions)
            or len(set(actions)) != len(actions)):
        raise EvidenceError("Invalid action sequence")
    windows = manifest.get("windows")
    if not isinstance(windows, list) or not 1 <= len(windows) <= 5:
        raise EvidenceError("Invalid window count")
    expected_windows = [("calibration", "Calibration")]
    if len(actions) > 1:
        expected_windows += [("baseline", actions[1])] + [("action", a) for a in actions[1:]]
    actual_windows = [(w.get("kind"), w.get("action")) for w in windows]
    if actual_windows != expected_windows[:len(windows)]:
        raise EvidenceError("Windows differ from requested action order")
    global_issues = []
    if (manifest.get("status") != "captured" or manifest.get("failure_reasons") != []
            or actual_windows != expected_windows):
        global_issues.append("recorder_batch_incomplete")
    reports = []
    for window in windows:
        issues = []
        if window.get("status") != "captured" or window.get("failure_reasons") != []:
            issues.append("recorder_window_incomplete")
        timing, timing_issues = window_timing(window, frequency, duration)
        issues.extend(timing_issues)
        sessions = window.get("sessions")
        if not isinstance(sessions, list) or not 1 <= len(sessions) <= 2:
            raise EvidenceError("Invalid session count")
        session_reports = []
        kinds = []
        for session in sessions:
            kind = session.get("kind")
            kinds.append(kind)
            if kind not in ("object", "acpi") or kinds.count(kind) != 1:
                raise EvidenceError("Unexpected or repeated session kind")
            session_issues = []
            if (session.get("before_start") or {}).get("status") != 4201:
                session_issues.append("preexisting_session_not_excluded")
            for stage in ("start", "enable", "last_query", "stop"):
                if (not isinstance(session.get(stage), dict) or type(session[stage].get("status")) is not int
                        or session[stage]["status"] != 0):
                    session_issues.append(stage + "_not_confirmed")
            for stage in ("last_query", "stop"):
                counters = session.get(stage) or {}
                for counter in ("events_lost", "log_buffers_lost", "realtime_buffers_lost"):
                    if type(counters.get(counter)) is not int or counters[counter] != 0:
                        session_issues.append(stage + "_" + counter + "_not_zero")
            if (session.get("after_stop") or {}).get("status") != 4201:
                session_issues.append("session_absence_unconfirmed")
            try:
                previous_qpc = 0
                for stage_name in ("before_start", "start", "enable", "last_query", "stop", "after_stop"):
                    stage = session.get(stage_name) or {}
                    qpc = integer(stage.get("qpc"))
                    utc = time_value(stage.get("utc"))
                    if qpc < previous_qpc or timing is None:
                        raise EvidenceError("Unconfirmed session order")
                    if abs((utc - timing["start_utc"]).total_seconds() - (qpc - timing["start_qpc"]) / frequency) > 0.25:
                        raise EvidenceError("Inconsistent session clocks")
                    previous_qpc = qpc
                if not session["enable"]["qpc"] <= timing["start_qpc"] <= timing["end_qpc"] <= session["stop"]["qpc"]:
                    raise EvidenceError("Shared interval outside session")
            except (ValueError, TypeError, KeyError, OverflowError):
                session_issues.append("session_timing_unconfirmed")
            cap = CAPS_MIB[version][kind] * 1024 * 1024
            expected_provider = "febd7460-3d1d-47eb-af49-c9eeb1e146f2" if kind == "object" else ACPI
            if (session.get("provider_guid") != expected_provider or session.get("keyword") != (2 if kind == "object" else 1)
                    or session.get("level") != (5 if kind == "object" else 4)
                    or session.get("file_cap_bytes") != cap):
                session_issues.append("provider_contract_not_confirmed")
            try:
                etl = contained_file(path.parent, session.get("file"))
            except FileNotFoundError:
                session_issues.append("etl_missing")
                session_reports.append({"kind": kind, "target_observations": 0, "observation": "not_inspected",
                                        "integrity_issues": session_issues})
                issues.extend(kind + ":" + issue for issue in session_issues)
                continue
            expected_hash = session.get("sha256")
            if (type(session.get("bytes")) is not int or not 0 < session["bytes"] < cap
                    or etl.stat().st_size != session["bytes"] or sha256(etl, cap) != expected_hash):
                raise EvidenceError("ETL differs from recorder manifest")
            try:
                export = contained_file(path.parent, session["file"] + ".jsonl")
            except FileNotFoundError:
                session_issues.append("offline_export_missing")
                session_reports.append({"kind": kind, "target_observations": 0, "observation": "not_inspected",
                                        "integrity_issues": session_issues})
                issues.extend(kind + ":" + issue for issue in session_issues)
                continue
            markers = prepare_markers(path.parent, run_id, window, session, timing) if kind == "object" else []
            evidence = analyze_export(export, markers)
            if evidence["etl_sha256"] != expected_hash or evidence["etl_bytes"] != session["bytes"]:
                raise EvidenceError("Export does not match manifest ETL")
            for name in ("unsupported_object_records", "unsupported_acpi_records", "conflicting_type_names",
                         "missing_or_ambiguous_file_type", "out_of_order_events"):
                if evidence["counts"].get(name, 0):
                    session_issues.append(name)
            if kind == "object":
                expected_markers = ["held"] if window.get("kind") == "baseline" else ["held", "transient"]
                if sorted(m.get("kind", "") for m in markers) != expected_markers:
                    session_issues.append("missing_marker_controls")
                if not all(marker["valid"] for marker in evidence["markers"]):
                    session_issues.append("marker_control_failed")
                if evidence["unknown_type_rows"]:
                    session_issues.append("unknown_object_types")
            elif not evidence["counts"].get("method_start") or not evidence["counts"].get("method_finish"):
                session_issues.append("no_normal_method_control")
            evidence["kind"] = kind
            evidence["integrity_issues"] = session_issues
            session_reports.append(evidence)
            issues.extend(kind + ":" + issue for issue in session_issues)
        expected_kinds = ["object"] if window.get("kind") == "baseline" else ["acpi", "object"]
        if sorted(kinds) != expected_kinds:
            issues.append("missing_session")
        reports.append({"action": window.get("action"), "kind": window.get("kind"),
                        "collection_integrity": "inconclusive" if issues else "controls_valid",
                        "integrity_issues": issues, "sessions": session_reports,
                        "independent_stimulus_confirmation": "required_external_review"})
    observed = any(s["target_observations"] for w in reports for s in w["sessions"])
    return {"schema": "openblade.mailbox-observation-analysis", "schema_version": 1,
            "manifest_sha256": hashlib.sha256(text).hexdigest(), "run_id": manifest.get("run_id"),
            "recorder_status": manifest.get("status"), "windows": reports,
            "collection_integrity": "inconclusive" if global_issues or any(w["integrity_issues"] for w in reports) else "controls_valid",
            "integrity_issues": global_issues,
            "observation": "target_observed" if observed else "no_target_observed",
            "assessment": "requires_review", "acpi_target_coverage": "unresolved",
            "hardware_write_authority": False,
            "limits": ["Unnamed and unpaired handles remain explicit coverage limits.",
                       "Operator marks do not independently confirm the physical stimulus.",
                       "Trace silence cannot establish unmeasured ACPI entry coverage or a hardware lock.",
                       "No automatic pass, probability estimate or write admission is produced."]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--manifest", type=Path)
    mode.add_argument("--export", type=Path, help="Inspect one export without claiming marker/collection coverage")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        report = analyze_manifest(args.manifest) if args.manifest else analyze_export(args.export)
        write_new_json(args.output, report)
    except (ValueError, OSError, TypeError, KeyError, AttributeError, struct.error) as error:
        print(f"Inspection failed: {error}", file=sys.stderr)
        return 2
    print(json.dumps({"output": str(args.output), "observation": report["observation"],
                      "assessment": "requires_review"}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
