#!/usr/bin/env python3
"""Verify exact SO6120 RZ09-0581 regional RGB maps from inert HEX/binary data.

Only standard-library Python and the repository's Intel HEX parser are used.
No device, service, updater, vendor executable, DLL or network is accessed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from blade_firmware.formats import decode_intel_hex

FIXTURE = Path(__file__).resolve().parents[2] / "src/OpenBlade.Contracts/Fixtures/Blade16KeyboardLightingLayout.json"
FIXTURE_SHA256 = "187711408fc0667273cabb19a1cb73b57ca53f98688993ba76e7352a8f2ae0fb"
IMAGE_SHA256 = "90bbe0d8dd1ca31614a2465c7c8821347b2a667606c39bedadbd59b98886344f"
BRANCHES = {
    0x5CD8: (0xB345, 0xB1CD, 47), 0x5CE8: (0xB915, 0xB675, 84),
    0x5D00: (0xBDB5, 0xBC45, 46), 0x5D10: (0xD1B5, 0xD015, 52),
    0x5D22: (0xC7BD, 0xC26D, 56), 0x5D2E: (0xC7BD, 0xC42D, 59),
    0x5D3A: (0xC7BD, 0xC605, 55), 0x5D46: (0xCCE5, 0xCAED, 63),
    0x5D56: (0xC7BD, 0xC0E5, 49),
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def word(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def load_fixture():
    raw = FIXTURE.read_bytes()
    require(sha(raw) == FIXTURE_SHA256, "Committed fixture SHA256 mismatch")
    fixture = json.loads(raw)
    require((fixture["model"], fixture["vendorId"], fixture["productId"],
             fixture["rows"], fixture["columns"], fixture["recordSize"]) ==
            ("RZ09-0581", 0x1532, 0x02E0, 6, 17, 8), "Fixture identity mismatch")
    return fixture


def verify_image(data):
    fixture = load_fixture()
    require(len(data) == 57764 and sha(data) == IMAGE_SHA256, "Decoded image size or SHA256 mismatch")
    require(data[0xDDBC:0xDDC0] == bytes.fromhex("3215e002"), "Exact USB 1532:02E0 identity mismatch")
    for proof in fixture["instructionWindows"]:
        start, size = proof["offset"], proof["length"]
        require(0 <= start < start + size <= len(data)
                and sha(data[start:start + size]) == proof["sha256"],
                f'Instruction/descriptor window mismatch: {proof["name"]}')
    require(word(data, 0x5D80) + 4 == 0x10001990
            and word(data, 0x7568) + 4 == 0x10001990,
            "Selector and layout getter no longer share a stored byte")
    require(word(data, 0x5D6C) == 0x1000010C
            and word(data, 0x6B3C) == 0x1000010C,
            "Selector and renderer descriptor state differ")
    pointers = {0x5D84: 0xC7BD, 0x5D8C: 0xB345, 0x5D90: 0xB915,
                0x5D98: 0xBDB5, 0x5D9C: 0xD1B5, 0x5DAC: 0xCCE5,
                0x7160: 0x10001A76, 0x490C: 0x10001A76,
                0x4910: 0x10000CC2, 0x6B40: 0x10000CC2}
    require(all(word(data, at) == expected for at, expected in pointers.items()),
            "Reviewed table/frame pointers changed")
    require(data[0x5CC4] == 17, "Selector switch bound changed")
    actual_selector = []
    for value in range(256):
        branch = (0x5CC5 + data[0x5CC5 + min(value, 17)] * 2) & ~1
        require(branch in BRANCHES, "Unknown selector destination")
        table, auxiliary, count = BRANCHES[branch]
        actual_selector.append(dict(layoutId=value, branchOffset=branch, tableOffset=table,
                                    auxiliaryTableOffset=auxiliary, auxiliaryRecordCount=count))
    require(actual_selector == fixture["selector"]["allByteValues"], "Selector coverage differs from fixture")
    tables = {}
    for table in fixture["tables"]:
        start = table["offset"]
        raw = data[start:start + 816]
        usages = list(raw[::8])
        require(len(usages) == 102 and sha(raw) == table["sha256"]
                and usages == table["hidUsages"], f"Descriptor map mismatch at {start:#x}")
        require([i for i, usage in enumerate(usages) if not usage] == table["holes"],
                "Descriptor hole mask mismatch")
        require(len({x for x in usages if x}) == table["nonzeroCount"], "Duplicate/missing usage")
        tables[start] = usages
    require(len(tables) == 6, "Regional map coverage incomplete")
    union_holes = [slot for slot in range(102) if not any(t[slot] for t in tables.values())]
    require(union_holes == fixture["unionHoles"] == [0, 17, 34, 51, 65, 68, 81, 85, 92],
            "Union hole mask mismatch")
    for auxiliary in fixture["auxiliaryTables"]:
        start, count = auxiliary["offset"], auxiliary["count"]
        raw = data[start:start + count * 8]
        require(sha(raw) == auxiliary["sha256"], "Auxiliary descriptor hash mismatch")
        table_offset, expected_start, expected_count = BRANCHES[auxiliary["branchOffset"]]
        require((start, count) == (expected_start, expected_count), "Auxiliary selection mismatch")
        slots = set()
        for i in range(count):
            row, column = raw[i * 8 + 2:i * 8 + 4]
            require(row < 6 and column < 17 and tables[table_offset][row * 17 + column] != 0,
                    "Auxiliary RGB reference escapes the selected map")
            slots.add(row * 17 + column)
        require(sorted(slots) == auxiliary["rgbSlots"], "Auxiliary RGB slots mismatch")
    require(word(data, 0xDF30) == 0xDF00 and word(data, 0xDF34) == 23
            and data[0xDF00:0xDF17] == bytes.fromhex("050c0901a1010600ff0902150025017508967601b101c0"),
            "Interface 3 Feature report geometry mismatch")
    return {"model": "RZ09-0581", "usbIdentity": "1532:02E0", "assertions": "Passed",
            "imageSha256": sha(data), "fixtureSha256": FIXTURE_SHA256, "tableCount": 6,
            "selectorValuesVerified": 256, "unionHoleSlots": union_holes,
            "physicalValidation": False, "installedMcuAttestation": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path, help="Pinned Intel HEX or decoded binary")
    parser.add_argument("--carrier", type=Path, help="Optional pinned original carrier, read only")
    parser.add_argument("--output", type=Path, help="New JSON result, refuses to overwrite")
    args = parser.parse_args()
    try:
        fixture = load_fixture()
        raw = args.image.read_bytes()
        if args.image.suffix.lower() in (".hex", ".ihex", ".ihx"):
            require(sha(raw) == fixture["source"]["hexSha256"], "Intel HEX SHA256 mismatch")
            decoded = decode_intel_hex(raw)
            require(decoded.base_address == 0, "Intel HEX base mismatch")
            data = decoded.data
        else:
            data = raw
        if args.carrier:
            carrier = args.carrier.read_bytes()
            require(len(carrier) == fixture["source"]["carrierLength"]
                    and sha(carrier) == fixture["source"]["carrierSha256"], "Carrier size or SHA256 mismatch")
        result = json.dumps(verify_image(data), indent=2) + "\n"
        if args.output:
            with args.output.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(result)
        else:
            print(result, end="")
    except (OSError, ValueError, KeyError, struct.error) as error:
        print(f"Verification failed: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
