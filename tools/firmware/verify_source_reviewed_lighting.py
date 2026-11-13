#!/usr/bin/env python3
"""Check the pinned offline exact-model MCU lighting evidence; never touches hardware.

Inputs are treated as data. The optional carrier is hash-checked before bounded
NSIS extraction. Instruction windows contain hashes, not redistributed vendor
bytes. This checks source identity and reviewed offsets, not running firmware,
physical output, runtime admission, or successful mutation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

from blade_firmware.formats import decode_intel_hex, extract_nsis_block

DEFAULT_EVIDENCE = Path(__file__).resolve().parents[2] / "evidence/source-reviewed-blades/2026-11-13-modern-mcu-lighting.json"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def thumb_bl_target(data: bytes, address: int) -> int:
    """Decode a Thumb BL immediate, requiring the expected instruction form."""
    first, second = struct.unpack_from("<HH", data, address)
    if first & 0xF800 != 0xF000 or second & 0xD000 != 0xD000:
        raise ValueError(f"No Thumb BL at 0x{address:X}")
    sign = (first >> 10) & 1
    j1, j2 = (second >> 13) & 1, (second >> 11) & 1
    i1, i2 = 1 ^ j1 ^ sign, 1 ^ j2 ^ sign
    displacement = (sign << 24) | (i1 << 23) | (i2 << 22) | ((first & 0x3FF) << 12) | ((second & 0x7FF) << 1)
    if sign:
        displacement -= 1 << 25
    return address + 4 + displacement


def thumb_literal(data: bytes, address: int) -> int:
    instruction, = struct.unpack_from("<H", data, address)
    if instruction & 0xF800 != 0x4800:
        raise ValueError(f"Expected Thumb literal load at 0x{address:X}")
    literal = ((address + 4) & ~3) + (instruction & 0xFF) * 4
    return struct.unpack_from("<I", data, literal)[0]


def verify(profile: dict, firmware: bytes, carrier: bytes | None = None) -> dict:
    pin = profile["firmware"]
    if firmware.lstrip().startswith(b":"):
        if digest(firmware) != pin["hexSha256"]:
            raise ValueError("HEX SHA-256 mismatch")
        image = decode_intel_hex(firmware)
        if image.base_address != 0:
            raise ValueError("Expected exact base-zero MCU image")
        data = image.data
    else:
        data = firmware
    if len(data) != pin["decodedBytes"] or digest(data) != pin["decodedSha256"]:
        raise ValueError("Decoded MCU size/SHA-256 mismatch")
    for site in profile["sites"]:
        address, length = int(site["offset"], 0), site["byteCount"]
        if address < 0 or length <= 0 or address + length > len(data):
            raise ValueError(f"Invalid reviewed window: {site['name']}")
        if digest(data[address:address + length]) != site["sha256"]:
            raise ValueError(f"Reviewed instruction window mismatch: {site['name']}")
    dispatch = profile["dispatch"]
    if "tableOffset" in dispatch:
        table = int(dispatch["tableOffset"], 0)
        index = 15 - dispatch["classSubtract"]
        if index < 0 or index >= data[table]:
            raise ValueError("Class0F outside dispatcher table")
        destination = ((table + 1) + 2 * data[table + 1 + index]) & ~1
        if destination != int(dispatch["class15Destination"], 0):
            raise ValueError("Class0F dispatch destination mismatch")
        branch = destination
    else:
        branch = int(dispatch["branchOffset"], 0)
    if thumb_bl_target(data, branch) != int(dispatch["class15Handler"], 0):
        raise ValueError("Class0F BL handler mismatch")
    for switch in profile["effectSwitches"].values():
        table = int(switch["tableOffset"], 0)
        if data[table] != switch["count"]:
            raise ValueError("Effect switch entry count mismatch")
        for mode, expected in switch["cases"].items():
            index = int(mode)
            actual = ((table + 1) + 2 * data[table + 1 + index]) & ~1
            if actual != int(expected, 0):
                raise ValueError("Exact effect switch target mismatch")
    active = profile["activeStateProof"]
    active_base = int(active["activeBase"], 0)
    saved_base = int(active["savedBase"], 0)
    if active_base == saved_base:
        raise ValueError("Active and saved bank aliases must remain distinct")
    if thumb_literal(data, int(active["selectorActiveLiteralInstruction"], 0)) != active_base:
        raise ValueError("GET0 active-bank base mismatch")
    if thumb_literal(data, int(active["selectorSavedLiteralInstruction"], 0)) + active["selectorSavedLiteralAdd"] != saved_base:
        raise ValueError("Saved-bank base mismatch")
    if thumb_literal(data, int(active["consumerActiveLiteralInstruction"], 0)) + active["consumerActiveLiteralAdd"] != active_base:
        raise ValueError("Active renderer and GET0 bank differ")
    consumer = int(active["consumerEntry"], 0)
    if thumb_bl_target(data, int(active["bank0SetCompletionCall"], 0)) != consumer:
        raise ValueError("Volatile completion no longer calls active consumer")
    if thumb_bl_target(data, int(active["legacyActivationConsumerCall"], 0)) != consumer:
        raise ValueError("Legacy completion no longer calls active consumer")
    if thumb_bl_target(data, int(active["class03HandlerCall"], 0)) != int(active["class03Handler"], 0):
        raise ValueError("Legacy class03 dispatch changed")
    contract = profile["writeContract"]
    if contract["effectReadStorage"] != 0 or contract["brightnessStorage"] != 0 or contract["firmwareSavedBankConfirmationPermitted"]:
        raise ValueError("Saved bank cannot confirm active state")
    newer = profile["product"] in (695, 696, 711)
    if contract["sameOffBridgeRequired"] != newer or not contract["explicitDestinationAlwaysSubmitted"]:
        raise ValueError("Overlay cleanup transition contract changed")
    if newer and "Spectrum" in profile["admittedEffects"]:
        raise ValueError("Unexposed Spectrum policy/overlay cannot be confirmed")
    descriptor = next(site for site in profile["sites"] if site["name"] == "usbDeviceDescriptor")
    address = int(descriptor["offset"], 0)
    if data[address:address + 2] != b"\x12\x01":
        raise ValueError("Device descriptor geometry mismatch")
    if struct.unpack_from("<HH", data, address + 8) != (0x1532, profile["product"]):
        raise ValueError("MCU VID/PID does not match exact admitted model")
    if carrier is not None:
        if len(carrier) != pin["carrierBytes"] or digest(carrier) != pin["carrierSha256"]:
            raise ValueError("Carrier size/SHA-256 mismatch")
        extracted = extract_nsis_block(carrier, pin["nsisHexBlock"])
        if digest(extracted) != pin["hexSha256"]:
            raise ValueError("Pinned carrier-to-HEX membership mismatch")
        if decode_intel_hex(extracted).data != data:
            raise ValueError("Carrier and supplied MCU differ")
    return {
        "model": profile["model"], "usbIdentity": profile["usbIdentity"],
        "status": "PinnedOfflineSourceVerified", "instructionWindows": len(profile["sites"]),
        "carrierMembershipVerified": carrier is not None,
        "physicalValidation": False, "installedFirmwareAttested": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--firmware", required=True, type=Path)
    parser.add_argument("--carrier", type=Path)
    parser.add_argument("--evidence", type=Path, default=DEFAULT_EVIDENCE)
    args = parser.parse_args()
    try:
        evidence = json.loads(args.evidence.read_text(encoding="utf-8-sig"))
        matches = [item for item in evidence["models"] if item["model"] == args.model]
        if len(matches) != 1:
            raise ValueError("Model has no unique reviewed lighting profile")
        result = verify(matches[0], args.firmware.read_bytes(), args.carrier.read_bytes() if args.carrier else None)
    except (OSError, ValueError, KeyError, struct.error) as error:
        print(f"Source verification failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
