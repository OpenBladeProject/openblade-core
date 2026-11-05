"""Validated declarative model profiles. JSON never supplies executable code."""

import json
from pathlib import Path
import re

from . import FirmwareError
from .formats import MAX_IMAGE_SIZE, MAX_INPUT_SIZE

MAX_JSON_SIZE = 8 * 1024 * 1024
EVIDENCE = {"firmware-confirmed", "inferred", "physically-verified", "unknown"}
PROFILE_FIELDS = ("id", "label", "model", "component", "version", "architecture", "endianness")


def integer(value, name, minimum=0, maximum=0xffffffff):
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise FirmwareError(f"{name} must be an integer in {minimum}..{maximum}")
    return value


def text(value, name, maximum=16384):
    if not isinstance(value, str) or len(value) > maximum:
        raise FirmwareError(f"{name} must be a bounded string")
    return value


def _object(value, name, allowed, required=()):
    if not isinstance(value, dict) or set(value) - set(allowed) or set(required) - set(value):
        raise FirmwareError(f"Invalid {name} fields")
    return value


def _list(value, name, limit=32768):
    if not isinstance(value, list) or len(value) > limit:
        raise FirmwareError(f"{name} must be a bounded list")
    return value


def _hash(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
        raise FirmwareError("SHA-256 must be 64 lowercase hexadecimal characters")
    return value


def _evidence(value):
    if not isinstance(value, str) or value not in EVIDENCE:
        raise FirmwareError("Unsupported evidence status")
    return value


def _references(value):
    result = []
    for reference in _list(value, "references", 256):
        _object(reference, "reference", ("kind", "source", "address", "label"), ("kind", "source"))
        if reference["kind"] not in ("instruction", "table", "document"):
            raise FirmwareError("Unsupported reference kind")
        address = reference.get("address")
        if address is not None:
            integer(address, "reference address")
        result.append({"kind": reference["kind"], "source": text(reference["source"], "reference source"),
                       "address": address, "label": text(reference.get("label", ""), "reference label")})
    return result


def normalize_annotation(value):
    allowed = ("bank", "index", "address", "width_bits", "name", "category", "description", "access",
               "units", "evidence_status", "notes", "references", "bits")
    _object(value, "register annotation", allowed, ("address", "width_bits"))
    result = {"bank": value.get("bank"), "index": value.get("index"),
              "address": integer(value["address"], "register address"),
              "width_bits": integer(value["width_bits"], "register width", 8, 32)}
    if result["width_bits"] not in (8, 16, 32):
        raise FirmwareError("Register width must be 8, 16 or 32 bits")
    for key in ("bank", "index"):
        if result[key] is not None:
            integer(result[key], key, 0, 65535)
    if (result["bank"] is None) != (result["index"] is None):
        raise FirmwareError("Annotation bank and index must both be present or null")
    for key in ("name", "category", "description", "access", "units"):
        result[key] = text(value.get(key, ""), key)
    result["evidence_status"] = _evidence(value.get("evidence_status", "unknown"))
    result["notes"] = [text(note, "note") for note in _list(value.get("notes", []), "notes", 256)]
    result["references"] = _references(value.get("references", []))
    result["bits"] = []
    seen = set()
    for bit in _list(value.get("bits", []), "bits", 32):
        _object(bit, "bit", ("bit", "name", "description", "evidence_status", "references"), ("bit",))
        number = integer(bit["bit"], "bit", 0, result["width_bits"] - 1)
        if number in seen:
            raise FirmwareError("Duplicate register bit")
        seen.add(number)
        result["bits"].append({"bit": number, "name": text(bit.get("name", ""), "bit name"),
            "description": text(bit.get("description", ""), "bit description"),
            "evidence_status": _evidence(bit.get("evidence_status", "unknown")),
            "references": _references(bit.get("references", []))})
    result["bits"].sort(key=lambda item: item["bit"])
    return result


def validate_profile(value):
    """Return a normalized independent profile, rejecting ambiguous definitions."""
    _object(value, "profile", ("schema_version", *PROFILE_FIELDS, "image", "carriers", "bank_map",
        "thermal_tables", "annotations", "findings"), ("schema_version", *PROFILE_FIELDS, "image"))
    if type(value["schema_version"]) is not int or value["schema_version"] != 1:
        raise FirmwareError("Unsupported profile schema version")
    result = {"schema_version": 1}
    for key in PROFILE_FIELDS:
        result[key] = text(value[key], key, 512)
        if not result[key]:
            raise FirmwareError(f"Profile {key} must not be empty")
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,127}", result["id"]):
        raise FirmwareError("Invalid profile id")
    image = _object(value["image"], "image", ("sha256", "size", "base_address"), ("sha256", "size", "base_address"))
    result["image"] = {"sha256": _hash(image["sha256"]), "size": integer(image["size"], "image size", 1, MAX_IMAGE_SIZE),
                       "base_address": integer(image["base_address"], "image base address")}
    if result["image"]["base_address"] + result["image"]["size"] > 0x100000000:
        raise FirmwareError("Image address range overflows")
    result["carriers"] = []
    for carrier in _list(value.get("carriers", []), "carriers", 256):
        _object(carrier, "carrier", ("sha256", "size", "format", "block_index", "payload_format", "payload_sha256"),
                ("sha256", "size", "format", "block_index", "payload_format", "payload_sha256"))
        if carrier["format"] != "nsis-blocks-v1" or carrier["payload_format"] not in ("binary", "intel-hex"):
            raise FirmwareError("Unsupported carrier or payload format")
        result["carriers"].append({"sha256": _hash(carrier["sha256"]),
            "size": integer(carrier["size"], "carrier size", 1, MAX_INPUT_SIZE), "format": carrier["format"],
            "block_index": integer(carrier["block_index"], "block index", 0, 4095),
            "payload_format": carrier["payload_format"], "payload_sha256": _hash(carrier["payload_sha256"])})
    bank = value.get("bank_map")
    if bank is not None:
        fields = ("pointer_table_offset", "bank_count", "entries_per_bank", "entry_size", "metadata_offset",
                  "address_offset", "address_size", "metadata_size", "byte_order", "access_values")
        _object(bank, "bank map", fields, fields)
        bank = dict(bank)
        size = result["image"]["size"]
        for key in fields[:8]:
            integer(bank[key], key, 0 if "offset" in key else 1, size)
        if bank["address_size"] not in (1, 2, 4) or bank["byte_order"] not in ("little", "big"):
            raise FirmwareError("Unsupported bank address size or byte order")
        if bank["bank_count"] > 256 or bank["entries_per_bank"] > 4096 or bank["bank_count"] * bank["entries_per_bank"] > 32768:
            raise FirmwareError("Bank map exceeds row limits")
        if bank["pointer_table_offset"] + bank["bank_count"] * bank["address_size"] > size:
            raise FirmwareError("Bank pointer table exceeds the image")
        if bank["metadata_size"] > 8 or bank["metadata_offset"] + bank["metadata_size"] > bank["entry_size"] or bank["address_offset"] + bank["address_size"] > bank["entry_size"]:
            raise FirmwareError("Bank entry fields exceed the entry size")
        if bank["metadata_offset"] < bank["address_offset"] + bank["address_size"] and bank["metadata_offset"] + bank["metadata_size"] > bank["address_offset"]:
            raise FirmwareError("Bank metadata/address fields overlap")
        if not isinstance(bank["access_values"], dict) or len(bank["access_values"]) > 256:
            raise FirmwareError("Invalid bank access values")
        for key, label in bank["access_values"].items():
            if not isinstance(key, str) or not re.fullmatch(r"0|[1-9][0-9]{0,19}", key) or int(key) >= 1 << (bank["metadata_size"] * 8):
                raise FirmwareError("Invalid bank access metadata value")
            text(label, "access label", 256)
        result["bank_map"] = bank
    thermal = value.get("thermal_tables")
    if thermal is not None:
        fields = ("group_offsets", "mode_count", "table_size", "decoder", "mode_names")
        _object(thermal, "thermal tables", fields, fields)
        if thermal["decoder"] != "so6120-thermal-v1":
            raise FirmwareError("Unsupported thermal decoder")
        count = integer(thermal["mode_count"], "thermal mode count", 1, 32)
        table_size = integer(thermal["table_size"], "thermal table size", 512, 512)
        offsets = [integer(offset, "thermal group offset", 0, result["image"]["size"] - 1)
                   for offset in _list(thermal["group_offsets"], "thermal groups", 32)]
        intervals = sorted((offset, offset + count * table_size) for offset in offsets)
        if not intervals or any(end > result["image"]["size"] for _, end in intervals) or any(a[1] > b[0] for a, b in zip(intervals, intervals[1:])):
            raise FirmwareError("Thermal groups overlap or exceed the image")
        names = thermal["mode_names"]
        if not isinstance(names, dict) or len(names) > count:
            raise FirmwareError("Invalid thermal mode names")
        for key, label in names.items():
            if not isinstance(key, str) or not re.fullmatch(r"0|[1-9][0-9]*", key) or int(key) >= count:
                raise FirmwareError("Invalid thermal mode name index")
            text(label, "thermal mode name", 256)
        result["thermal_tables"] = {**thermal, "group_offsets": offsets}
    annotations = [normalize_annotation(item) for item in _list(value.get("annotations", []), "annotations")]
    seen = set()
    for annotation in annotations:
        identity = (annotation["bank"], annotation["index"]) if bank is not None else annotation["address"]
        if identity in seen:
            raise FirmwareError("Duplicate annotation identity")
        seen.add(identity)
        if bank is not None:
            if annotation["bank"] is None or annotation["bank"] >= bank["bank_count"] or annotation["index"] >= bank["entries_per_bank"]:
                raise FirmwareError("Annotation lies outside the bank map")
        elif annotation["bank"] is not None:
            raise FirmwareError("Bank annotations require a bank map")
    result["annotations"] = annotations
    result["findings"] = []
    for finding in _list(value.get("findings", []), "findings", 1024):
        _object(finding, "finding", ("title", "description", "evidence_status", "references"), ("title",))
        result["findings"].append({"title": text(finding["title"], "finding title"),
            "description": text(finding.get("description", ""), "finding description"),
            "evidence_status": _evidence(finding.get("evidence_status", "unknown")),
            "references": _references(finding.get("references", []))})
    return result


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise FirmwareError(f"Duplicate JSON field: {key}")
        result[key] = value
    return result


def read_json(path, limit=MAX_JSON_SIZE):
    path = Path(path)
    with path.open("rb") as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit:
        raise FirmwareError("JSON exceeds its size limit")
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_pairs,
                          parse_constant=lambda value: (_ for _ in ()).throw(FirmwareError("Non-finite JSON number")))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as error:
        raise FirmwareError("Malformed or excessively nested JSON") from error


def load_profiles(profile_files=()):
    paths = sorted((Path(__file__).parent.parent / "profiles").glob("*.json"))
    paths.extend(Path(path) for path in profile_files)
    result, ids, images, carriers = [], set(), set(), set()
    for path in paths:
        profile = validate_profile(read_json(path))
        if profile["id"] in ids or profile["image"]["sha256"] in images:
            raise FirmwareError("Duplicate or ambiguous profile identity")
        ids.add(profile["id"])
        images.add(profile["image"]["sha256"])
        for carrier in profile["carriers"]:
            if carrier["sha256"] in carriers or carrier["sha256"] in images:
                raise FirmwareError("Duplicate or ambiguous carrier identity")
            carriers.add(carrier["sha256"])
        result.append(profile)
    if images & carriers:
        raise FirmwareError("An input cannot identify both a carrier and an image")
    return sorted(result, key=lambda profile: profile["id"])
