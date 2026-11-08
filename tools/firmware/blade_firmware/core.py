"""Exact-image admission and deterministic offline firmware interpretation."""

from dataclasses import dataclass
import hashlib
from pathlib import Path

from . import FirmwareError, __version__
from .formats import Image, MAX_IMAGE_SIZE, MAX_INPUT_SIZE, decode_intel_hex, extract_nsis_block
from .profiles import PROFILE_FIELDS, _hash, _object, _references, _evidence, integer, text, normalize_annotation, validate_profile


def sha256(data):
    return hashlib.sha256(data).hexdigest()


@dataclass(frozen=True)
class InspectionResult:
    report: dict
    extracted_image: bytes | None


def inspect_file(path, profiles, profile_id=None):
    path = Path(path)
    with path.open("rb") as stream:
        data = stream.read(MAX_INPUT_SIZE + 1)
    return inspect_bytes(data, path.name, profiles, profile_id)


def inspect_bytes(data, source_name, profiles, profile_id=None):
    if not isinstance(data, bytes) or not data or len(data) > MAX_INPUT_SIZE:
        raise FirmwareError("Input is empty or exceeds the 64 MiB input limit")
    profiles = [validate_profile(profile) for profile in profiles]
    identities, image_hashes, carrier_hashes = set(), set(), set()
    for profile in profiles:
        if profile["id"] in identities or profile["image"]["sha256"] in image_hashes:
            raise FirmwareError("Duplicate or ambiguous profile identity")
        identities.add(profile["id"])
        image_hashes.add(profile["image"]["sha256"])
        for carrier in profile["carriers"]:
            if carrier["sha256"] in carrier_hashes:
                raise FirmwareError("Duplicate or ambiguous carrier identity")
            carrier_hashes.add(carrier["sha256"])
    if image_hashes & carrier_hashes:
        raise FirmwareError("Ambiguous carrier/image identity")
    if profile_id is not None and profile_id not in identities:
        raise FirmwareError(f"Unknown profile id: {profile_id}")
    source_hash = sha256(data)
    carrier_hits = [(profile, carrier) for profile in profiles for carrier in profile["carriers"]
                    if carrier["sha256"] == source_hash]
    extracted = False
    carrier_profile = None
    if carrier_hits:
        carrier_profile, carrier = carrier_hits[0]
        if len(data) != carrier["size"]:
            raise FirmwareError("Known carrier size mismatch")
        payload = extract_nsis_block(data, carrier["block_index"])
        if sha256(payload) != carrier["payload_sha256"]:
            raise FirmwareError("Extracted carrier payload hash mismatch")
        image = decode_intel_hex(payload) if carrier["payload_format"] == "intel-hex" else Image(payload, carrier_profile["image"]["base_address"], len(payload))
        source_format = carrier["format"]
        extracted = True
    else:
        image = None
        explicit_hex = Path(source_name).suffix.lower() in (".hex", ".ihex", ".ihx")
        if explicit_hex or (source_hash not in image_hashes and data.startswith(b":")):
            try:
                image = decode_intel_hex(data)
            except FirmwareError:
                # ':' is also an ordinary binary byte. Only a complete valid
                # document identifies unlabeled HEX; an explicit HEX filename
                # must instead report malformed data without a raw fallback.
                if explicit_hex:
                    raise
        if image is None:
            # A PE is a carrier, never an unknown raw image. No extraction on a guess.
            if source_hash not in image_hashes and data.startswith(b"MZ"):
                raise FirmwareError("Unknown executable carrier: an exact reviewed carrier hash is required")
            if len(data) > MAX_IMAGE_SIZE:
                raise FirmwareError("Raw image exceeds the 16 MiB image limit")
            image = Image(data, 0, len(data))
            source_format = "binary"
        else:
            source_format = "intel-hex"
            extracted = True
    image_hash = sha256(image.data)
    matches = [profile for profile in profiles if profile["image"]["sha256"] == image_hash]
    profile = matches[0] if matches else None
    if carrier_profile is not None and profile is not carrier_profile:
        raise FirmwareError("Carrier decoded image does not match its reviewed profile")
    if profile_id is not None and (profile is None or profile["id"] != profile_id):
        raise FirmwareError("Explicit profile mismatch: image hash does not match the selected profile")
    if profile:
        expected = profile["image"]
        if len(image.data) != expected["size"]:
            raise FirmwareError("Profile image size mismatch")
        if source_format == "binary":
            image = Image(image.data, expected["base_address"], image.populated_bytes)
        if image.base_address != expected["base_address"]:
            raise FirmwareError("Profile image base address mismatch")
    warnings = [] if profile else ["No exact hash-matched profile; metadata only. Registers and thermal tables were not interpreted."]
    registers = _decode_registers(image, profile) if profile else []
    thermal = _decode_thermal(image.data, profile.get("thermal_tables")) if profile else []
    report = {"schema_version": 1, "tool_version": __version__, "kind": "firmware-inspection", "offline": True,
        "profile": {key: profile[key] for key in PROFILE_FIELDS} if profile else None,
        "source": {"name": Path(source_name).name, "size": len(data), "sha256": source_hash, "format": source_format},
        "image": {"size": len(image.data), "sha256": image_hash, "base_address": image.base_address,
                  "populated_bytes": image.populated_bytes},
        "registers": registers, "thermal_tables": thermal, "findings": profile["findings"] if profile else [], "warnings": warnings}
    return InspectionResult(report, image.data if extracted else None)


def _decode_registers(image, profile):
    bank_map = profile.get("bank_map")
    if bank_map is None:
        return [{**annotation, "key": f"a{annotation['address']}", "metadata": None, "table_offset": None}
                for annotation in sorted(profile["annotations"], key=lambda item: item["address"])]
    size, order = bank_map["address_size"], bank_map["byte_order"]
    rows, intervals = [], []
    annotations = {(item["bank"], item["index"]): item for item in profile["annotations"]}
    metadata_size = bank_map["metadata_size"]
    pointer_start = bank_map["pointer_table_offset"]
    pointer_end = pointer_start + bank_map["bank_count"] * size
    for bank in range(bank_map["bank_count"]):
        pointer_offset = pointer_start + bank * size
        table = int.from_bytes(image.data[pointer_offset:pointer_offset + size], order) - image.base_address
        end = table + bank_map["entries_per_bank"] * bank_map["entry_size"]
        if table < 0 or end > len(image.data) or (table < pointer_end and end > pointer_start):
            raise FirmwareError("Bank table pointer is outside the image or overlaps its pointer table")
        if any(table < other_end and end > other_start for other_start, other_end in intervals):
            raise FirmwareError("Duplicate or overlapping bank table pointers")
        intervals.append((table, end))
        for index in range(bank_map["entries_per_bank"]):
            offset = table + index * bank_map["entry_size"]
            metadata_offset = offset + bank_map["metadata_offset"]
            metadata = int.from_bytes(image.data[metadata_offset:metadata_offset + metadata_size], order)
            address_offset = offset + bank_map["address_offset"]
            address = int.from_bytes(image.data[address_offset:address_offset + size], order)
            access = bank_map["access_values"].get(str(metadata), "unknown")
            annotation = annotations.get((bank, index))
            if annotation is not None:
                if annotation["address"] != address or (annotation["access"] and annotation["access"] != access):
                    raise FirmwareError("Reviewed annotation does not match the bank table address/access")
                row = {**annotation, "access": access}
            else:
                row = normalize_annotation({"bank": bank, "index": index, "address": address, "width_bits": 8,
                    "name": "Unclassified register", "category": "unclassified", "access": access})
            rows.append({**row, "key": f"b{bank}:i{index}", "metadata": metadata, "table_offset": offset})
    return rows


def _decode_thermal(image, specification):
    if specification is None:
        return []
    tables = []
    for group, base in enumerate(specification["group_offsets"], 1):
        for mode in range(specification["mode_count"]):
            offset = base + mode * specification["table_size"]
            data = image[offset:offset + specification["table_size"]]
            last, fans = data[0] >> 3, data[0] & 7
            sensors = [sensor for sensor in range(8) if data[1] & (1 << sensor)]
            stride = fans + 2 * len(sensors) + 5
            stage_end = 8 + (last + 1) * stride
            extension = int.from_bytes(data[3:5], "little")
            if not fans or stage_end > len(data) or extension < stage_end or extension + 0x26 >= len(data):
                raise FirmwareError("Malformed SO6120 thermal table stage/extension bounds")
            stages = []
            for stage in range(last + 1):
                start = 8 + stage * stride
                row = data[start:start + stride]
                stages.append({"stage": stage, "offset": offset + start, "targets_rpm": [value * 50 for value in row[:fans]],
                    "sensor_bounds": {str(sensor): list(row[fans + 2 * i:fans + 2 * i + 2]) for i, sensor in enumerate(sensors)},
                    "tail": list(row[-5:])})
            minimum, maximum = data[extension + 0x21] * 100, data[extension + 0x26] * 100
            if minimum > maximum:
                raise FirmwareError("SO6120 thermal manual minimum exceeds maximum")
            tables.append({"group": group, "mode": mode, "mode_name": specification["mode_names"].get(str(mode), f"Mode {mode}"),
                "offset": offset, "sha256": sha256(data), "fan_count": fans, "sensors": sensors, "stages": stages,
                "manual_minimum_rpm": minimum, "manual_maximum_rpm": maximum})
    return tables


def diff_reports(before, after):
    """Compare normalized semantic records, retaining profile provenance."""
    before, after = validate_report(before), validate_report(after)
    changes = {}
    for field, key in (("registers", "key"), ("thermal_tables", None)):
        def identity(item):
            return item[key] if key else f"g{item['group']}:m{item['mode']}"
        old = {identity(item): item for item in before[field]}
        new = {identity(item): item for item in after[field]}
        changes[field] = [{"key": name, "before": old.get(name), "after": new.get(name)}
                          for name in sorted(old.keys() | new.keys()) if old.get(name) != new.get(name)]
    return {"schema_version": 1, "tool_version": __version__, "kind": "firmware-diff", "offline": True,
        "before": {"profile": before["profile"], "image": before["image"]},
        "after": {"profile": after["profile"], "image": after["image"]},
        "same_profile": before["profile"] == after["profile"], "changes": changes,
        "findings_changed": before["findings"] != after["findings"], "warnings_changed": before["warnings"] != after["warnings"]}


def validate_report(report):
    """Return validated data with canonical optional register fields, without mutation."""
    if not isinstance(report, dict) or type(report.get("schema_version")) is not int or report["schema_version"] != 1 or report.get("kind") != "firmware-inspection" or report.get("offline") is not True:
        raise FirmwareError("Unsupported firmware report schema")
    required = ("tool_version", "profile", "source", "image", "registers", "thermal_tables", "findings", "warnings")
    if any(key not in report for key in required):
        raise FirmwareError("Incomplete firmware report")
    if report["profile"] is not None and (not isinstance(report["profile"], dict) or set(report["profile"]) != set(PROFILE_FIELDS) or any(not isinstance(value, str) for value in report["profile"].values())):
        raise FirmwareError("Malformed report profile")
    text(report["tool_version"], "tool version", 128)
    source = _object(report["source"], "report source", ("name", "size", "sha256", "format"), ("name", "size", "sha256", "format"))
    text(source["name"], "source name", 1024)
    integer(source["size"], "source size", 1, MAX_INPUT_SIZE)
    _hash(source["sha256"])
    if source["format"] not in ("binary", "intel-hex", "nsis-blocks-v1"):
        raise FirmwareError("Unsupported report source format")
    image = _object(report["image"], "report image", ("size", "sha256", "base_address", "populated_bytes"), ("size", "sha256", "base_address", "populated_bytes"))
    integer(image["size"], "image size", 1, MAX_IMAGE_SIZE)
    integer(image["base_address"], "image base address")
    integer(image["populated_bytes"], "populated image bytes", 1, image["size"])
    if image["base_address"] + image["size"] > 0x100000000:
        raise FirmwareError("Report image address range overflows")
    _hash(image["sha256"])
    for field in ("registers", "thermal_tables", "findings", "warnings"):
        if not isinstance(report[field], list) or len(report[field]) > 32768:
            raise FirmwareError(f"Malformed report {field}")
    seen, normalized_registers = set(), []
    for row in report["registers"]:
        if not isinstance(row, dict) or not isinstance(row.get("key"), str) or row["key"] in seen:
            raise FirmwareError("Malformed or duplicate report register key")
        seen.add(row["key"])
        annotation = normalize_annotation({key: value for key, value in row.items() if key not in ("key", "metadata", "table_offset")})
        expected_key = f"a{annotation['address']}" if annotation["bank"] is None else f"b{annotation['bank']}:i{annotation['index']}"
        if row["key"] != expected_key or "metadata" not in row or "table_offset" not in row:
            raise FirmwareError("Report register key/metadata does not match its identity")
        if row["metadata"] is not None:
            integer(row["metadata"], "register metadata", 0, (1 << 64) - 1)
        if row["table_offset"] is not None:
            integer(row["table_offset"], "register table offset", 0, image["size"] - 1)
        normalized_registers.append({**annotation, "key": row["key"], "metadata": row["metadata"],
                                     "table_offset": row["table_offset"]})
    seen = set()
    for row in report["thermal_tables"]:
        if not isinstance(row, dict) or type(row.get("group")) is not int or type(row.get("mode")) is not int:
            raise FirmwareError("Malformed thermal report identity")
        identity = row["group"], row["mode"]
        if identity in seen:
            raise FirmwareError("Duplicate thermal report identity")
        seen.add(identity)
        expected = ("group", "mode", "mode_name", "offset", "sha256", "fan_count", "sensors", "stages", "manual_minimum_rpm", "manual_maximum_rpm")
        _object(row, "thermal report table", expected, expected)
        integer(row["group"], "thermal group", 1, 32)
        integer(row["mode"], "thermal mode", 0, 31)
        text(row["mode_name"], "thermal mode name", 256)
        offset = integer(row["offset"], "thermal table offset", 0, image["size"] - 1)
        if offset + 512 > image["size"]:
            raise FirmwareError("Thermal report table exceeds image")
        _hash(row["sha256"])
        fans = integer(row["fan_count"], "thermal fan count", 1, 7)
        sensors = row["sensors"]
        if not isinstance(sensors, list) or len(sensors) > 8:
            raise FirmwareError("Malformed thermal sensors")
        for sensor in sensors:
            integer(sensor, "sensor", 0, 7)
        if len(set(sensors)) != len(sensors):
            raise FirmwareError("Duplicate thermal sensor")
        stages = row["stages"]
        if not isinstance(stages, list) or not 1 <= len(stages) <= 32:
            raise FirmwareError("Malformed thermal stages")
        for index, stage in enumerate(stages):
            fields = ("stage", "offset", "targets_rpm", "sensor_bounds", "tail")
            _object(stage, "thermal stage", fields, fields)
            if integer(stage["stage"], "stage", 0, 31) != index:
                raise FirmwareError("Thermal stages are not sequential")
            integer(stage["offset"], "stage offset", offset + 8, offset + 511)
            if not isinstance(stage["targets_rpm"], list) or len(stage["targets_rpm"]) != fans:
                raise FirmwareError("Malformed thermal targets")
            for target in stage["targets_rpm"]:
                integer(target, "fan target", 0, 12750)
            bounds = stage["sensor_bounds"]
            if not isinstance(bounds, dict) or set(bounds) != {str(sensor) for sensor in sensors}:
                raise FirmwareError("Malformed thermal sensor bounds")
            for pair in bounds.values():
                if not isinstance(pair, list) or len(pair) != 2:
                    raise FirmwareError("Malformed thermal sensor bound pair")
                for bound in pair:
                    integer(bound, "sensor bound", 0, 255)
            if not isinstance(stage["tail"], list) or len(stage["tail"]) != 5:
                raise FirmwareError("Malformed thermal stage tail")
            for value in stage["tail"]:
                integer(value, "stage tail", 0, 255)
        integer(row["manual_minimum_rpm"], "manual minimum", 0, 25500)
        integer(row["manual_maximum_rpm"], "manual maximum", row["manual_minimum_rpm"], 25500)
    normalized_findings = []
    for finding in report["findings"]:
        fields = ("title", "description", "evidence_status", "references")
        _object(finding, "report finding", fields, fields)
        text(finding["title"], "finding title")
        text(finding["description"], "finding description")
        _evidence(finding["evidence_status"])
        normalized_findings.append({**finding, "references": _references(finding["references"])})
    for warning in report["warnings"]:
        text(warning, "report warning")
    if report["profile"] is None and any(report[field] for field in ("registers", "thermal_tables", "findings")):
        raise FirmwareError("An unknown image report cannot claim profile interpretation")
    return {**report, "registers": normalized_registers, "findings": normalized_findings}
