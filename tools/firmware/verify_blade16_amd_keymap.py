#!/usr/bin/env python3
"""Verify exact SO6110 RZ09-0528 regional RGB maps from inert HEX/binary data.

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

FIXTURE = Path(__file__).resolve().parents[2] / "src/OpenBlade.Contracts/Fixtures/Blade16AmdKeyboardLightingLayout.json"
FIXTURE_SHA256 = "f8f0ba8d85804187b3b063363460bc3e867bc1a147d9ae2895970a62de1a1013"
IMAGE_SHA256 = "371c590b86e578720a063bd2e1ee6795a233dfb54dc09613472e4cfbe4d648c1"
BRANCHES = {
    0x5B94: (0xB169, 0xAFF1, 47), 0x5BA4: (0xB739, 0xB499, 84),
    0x5BC0: (0xBBD9, 0xBA69, 46), 0x5BD0: (0xCFD9, 0xCE39, 52),
    0x5BE2: (0xC5E1, 0xC091, 56), 0x5BEE: (0xC5E1, 0xC251, 59),
    0x5BFA: (0xC5E1, 0xC429, 55), 0x5C06: (0xCB09, 0xC911, 63),
    0x5C16: (0xC5E1, 0xBF09, 49),
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
            ("RZ09-0528", 0x1532, 0x02C6, 6, 17, 8), "Fixture identity mismatch")
    return fixture


def verify_image(data):
    fixture = load_fixture()
    require(len(data) == 59420 and sha(data) == IMAGE_SHA256, "Decoded image size or SHA256 mismatch")
    require(data[0xDBE0:0xDBE4] == bytes.fromhex("3215c602"), "Exact USB 1532:02C6 identity mismatch")
    for proof in fixture["instructionWindows"]:
        start, size = proof["offset"], proof["length"]
        require(0 <= start < start + size <= len(data)
                and sha(data[start:start + size]) == proof["sha256"],
                f'Instruction/descriptor window mismatch: {proof["name"]}')
    require(word(data, 0x5C50) + 4 == 0x100017F0
            and word(data, 0x72A8) + 4 == 0x100017F0,
            "Selector and layout getter no longer share a stored byte")
    require(word(data, 0x5C3C) == 0x10000108
            and word(data, 0x6A14) == 0x10000108,
            "Selector and renderer descriptor state differ")
    pointers = {0x5C54: 0xC5E1, 0x5C5C: 0xB169, 0x5C60: 0xB739,
                0x5C6C: 0xBBD9, 0x5C70: 0xCFD9, 0x5C80: 0xCB09,
                0x8658: 0x100018D5, 0x48D0: 0x100018D5,
                0x48D4: 0x10000C66, 0x6A18: 0x10000C66,
                0x48C4: 0x100001BD, 0x48C8: 0x10000179, 0x48CC: 0x10000188,
                0x21D8: 0xE6F8, 0x21DC: 0xE718, 0xE6F8: 0xE718,
                0xE6FC: 0x100000C0, 0xE700: 0x104, 0xE704: 0x44DE}
    require(all(word(data, at) == expected for at, expected in pointers.items()),
            "Reviewed table/frame/CRT pointers changed")
    require(data[0xE718 + 0x100001BD - 0x100000C0] == 1,
            "Standard staging source flag initialization changed")
    require(data[0x5B80] == 17, "Selector switch bound changed")
    actual_selector = []
    for value in range(256):
        branch = (0x5B81 + data[0x5B81 + min(value, 17)] * 2) & ~1
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
    descriptor = data[0xDBEA:0xDBEA + 94]
    require(word(data, 0xDE7C) == 0xDBEA and word(data, 0xDE80) == 94
            and descriptor == bytes.fromhex(
                "05010902a1010901a10005091901290515002501750195058102750195038103"
                "0600ff0940750895021581257f8102050109381581257f75089501810609300931"
                "16008026ff7f751095028106c00600ff0902150025017508955ab101c0"),
            "Interface 2 Feature report geometry mismatch")
    return {"model": "RZ09-0528", "usbIdentity": "1532:02C6", "assertions": "Passed",
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
