#!/usr/bin/env python3
"""Offline, exact-image PI4110 keyboard HID-to-light descriptor extraction.

No device, updater, network, native DLL or Ghidra is used. The committed report
contains derived map metadata and instruction-window hashes, not vendor images.
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

IMAGE_SIZE = 52612
IMAGE_SHA256 = "1ac3f05486d0bca5fbbb1a4a52b874943ad491a00afd298b8d6b857a49d98f11"
HEX_SHA256 = "4787f3c382d57c165dd2de099147b678070d0251e0febb5b6cc5a7902048bcd3"
CARRIER_SHA256 = "aed7039be5cc4ec3f166a2728b0c1b90839bae72c8a04150056654f64f20581f"
CARRIER_SIZE = 1439888
TABLES = (
    ("map-afe3", 0xAFE3, "1eb8905feeb1f78ee291814df44728143d891928ea2bfb75276499f756a97af1"),
    ("map-b313", 0xB313, "c6f9a2fdf904d40a88a486f3fb5baeb53a19399ec9496c3ffd6335e00b9c9f17"),
    ("map-b63b", 0xB63B, "3525ccca6f90f277032841c70616de625034369d4bfea4d38911f49a30b8a0c3"),
)
PROOFS = [{'offset': '0x67b0',
  'byteCount': 36,
  'sha256': '033bf3d244a836904ef74bc360e46ddeb7b65cae908a7ca0d151facef394ae7f',
  'meaning': 'Application main invokes settings and layout initialization before lighting '
             'operation'},
 {'offset': '0x5d88',
  'byteCount': 16,
  'sha256': 'a5dc7bd4fb0a19e5d25de95cef81e93b31cd760f8010f99b85e4e0111494e990',
  'meaning': 'Layout selector reads the stored edition byte and dispatches through the compact '
             'switch'},
 {'offset': '0x5da8',
  'byteCount': 4,
  'sha256': '3bdcc13bc73159a88e39fe8903837b49b86c79e36cb8ee4a1533f08edd56aea1',
  'meaning': 'Default-family selector stores map AFE3 at active descriptor pointer +24'},
 {'offset': '0x5dd2',
  'byteCount': 4,
  'sha256': '85d1afb3b21acd8e6754843589cf5d114cf408515d12c345efd2c61f835cf42c',
  'meaning': 'Selector12 stores map B63B at active descriptor pointer +24'},
 {'offset': '0x5dde',
  'byteCount': 4,
  'sha256': '756fa000ec1d6fa2d4f1679047fd793be88defb6be3b4d65b830aad649644bdc',
  'meaning': 'Other selector family stores map B313 at active descriptor pointer +24'},
 {'offset': '0x58c8',
  'byteCount': 50,
  'sha256': 'e248ae0bb79a4d86c56676ebea0ce57f0f22088996bb64575c991a0435b2ddfb',
  'meaning': 'Firmware HID lookup scans six rows by sixteen eight-byte records and returns loop '
             'row and column'},
 {'offset': '0x84be',
  'byteCount': 102,
  'sha256': '611d61ebe47cda863ebe50b54c6bef30a7c671f1bbbad8413e16420da88f16e3',
  'meaning': 'Standard custom-row command validates six rows and sixteen columns and copies RGB '
             'with row stride48 and column stride3'},
 {'offset': '0x682c',
  'byteCount': 390,
  'sha256': '49d0e8cd4ad651e408d67b449c19239179f56865d8acf809848f15995f70e34a',
  'meaning': 'Active renderer uses each six-by-sixteen descriptor record index to select the '
             'corresponding RGB input and LED driver channels'},
 {'offset': '0x634c',
  'byteCount': 66,
  'sha256': 'b30094f6e0d958039abaf8db1bc6afaae3499c9c9cc07ca4a8776c2e7ce10c38',
  'meaning': 'Startup animation separately consumes the six-by-twenty-two HID grid; that grid is '
             'not the command address map'},
 {'offset': '0x49c8',
  'byteCount': 152,
  'sha256': '6cc5b18a8cb28370acec07bccac13a71a332fe0ed7858cc06acce130759b65f6',
  'meaning': 'Retained lighting stage selects a same-size RGB frame copy before physical '
             'rendering'},
 {'offset': '0x4a8c',
  'byteCount': 56,
  'sha256': 'f28c42d8b3562c4fbe2ccbeafe9c9cb78acec319eae210b2a0f8fc75e792f4ab',
  'meaning': 'Custom frame handoff copies288 bytes without permutation into active six-by-sixteen '
             'RGB storage'},
 {'offset': '0x83c6',
  'byteCount': 42,
  'sha256': '9c0ea2c5ffef61fc62a8857c87537b18d0544e7a561b46c90f1b1054a53bf9ce',
  'meaning': 'Standard custom activation selects host frame mode and calls the RGB frame handoff'},
 {'offset': '0x7030',
  'byteCount': 8,
  'sha256': 'f4237a0519baabb02ce5cfb2967b31341958d09ef29378b15b1dd1ab8e08ab7e',
  'meaning': 'Class00 getter dispatch loads edition byte from stored settings+4 into a local'},
 {'offset': '0x71b0',
  'byteCount': 8,
  'sha256': 'b582f837ea8597d41a0437068576ad388a5e194db1b1ed1e5e8bc386533dc06a',
  'meaning': 'Class00 command86 returns that edition byte at payload0 and adjacent stored byte at '
             'payload1'},
 {'offset': '0xc454',
  'byteCount': 32,
  'sha256': 'd725f18832211452c948b9ef6721bde814b537a6157912377d91f5b315eb66b5',
  'meaning': 'USB report descriptor table binds four interface indices to report descriptor '
             'pointers and lengths'},
 {'offset': '0xc474',
  'byteCount': 109,
  'sha256': '9db6069211888d5f085c5b3d48a509f422d8b02ae0907b476209ba6557c9b2f5',
  'meaning': 'USB configuration declares interfaces0-3, HID classes, protocols, report lengths and '
             'endpoints'},
 {'offset': '0xcc64',
  'byteCount': 16,
  'sha256': '4f3830f1652cc763a0beafe32e68af28cd78e90bda499b30d51a80701f276ba0',
  'meaning': 'Application CRT copiesROMCC84 toRAM100000C0 with length0x100'},
 {'offset': '0xcd7c',
  'byteCount': 4,
  'sha256': 'a8d9e571a3f6f79da5fff4bda27926a1870031369ec137d6587305c8efec80d2',
  'meaning': 'Initialized byte atROMCD7D gives standard-frame source flagRAM100001B9 the value1'},
 {'offset': '0x5f4c',
  'byteCount': 72,
  'sha256': '5a407882ca4034c1ddda3bb30c6ec1712a9e00063dd0cba516fbc0a7e2bee8e7',
  'meaning': 'Later edition initialization writes capability record+4 pointer and+2 region class; '
             'preserves its+1 standard-frame-source flag'}]


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def word(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def verify_image(data):
    require(len(data) == IMAGE_SIZE, "Decoded image size mismatch")
    require(sha(data) == IMAGE_SHA256, "Decoded image SHA256 mismatch")
    require(data[0xC1C8:0xC1CC] == bytes.fromhex("3215c502"),
            "Exact USB identity 1532:02C5 is missing")
    for proof in PROOFS:
        start = int(proof["offset"], 16)
        require(sha(data[start:start + proof["byteCount"]]) == proof["sha256"],
                f'Instruction-site mismatch at {proof["offset"]}')
    require(word(data, 0x5E00) == 0x10000108, "Layout-state base changed")
    require(word(data, 0x5E14) == 0x1000186C, "Edition-state base changed")
    require([word(data, at) for at in (0x5E18, 0x5E2C, 0x5E28)] ==
            [entry[1] for entry in TABLES], "Layout pointer literals changed")
    require(data[0x5D98] == 13, "Edition switch bound changed")
    switch_targets = [(0x5D99 + data[0x5D99 + i] * 2) & ~1 for i in range(14)]
    require(switch_targets ==
            [0x5DA8, 0x5DA8, 0x5DDE, 0x5DDE, 0x5DDE, 0x5DA8,
             0x5DDE, 0x5DDE, 0x5DA8, 0x5DA8, 0x5DDE, 0x5DA8,
             0x5DD2, 0x5DDE], "Edition switch destinations changed")
    require(word(data, 0xCC64) == 0xCC84 and word(data, 0xCC68) == 0x100000C0
            and word(data, 0xCC6C) == 0x100, "Application CRT copy map changed")
    require(data[0xCD7D] == 1, "Standard frame-source default changed")
    variants = []
    bindings = []
    for layout_id, offset, expected_hash in TABLES:
        raw = data[offset:offset + 96 * 8]
        require(sha(raw) == expected_hash, f"{layout_id} descriptor hash mismatch")
        slots = list(raw[::8])
        lookup = {usage: slot for slot, usage in enumerate(slots) if usage}
        require(len(lookup) == sum(bool(v) for v in slots),
                f"{layout_id} contains duplicate nonzero usage bytes")
        variants.append({
            "layoutId": layout_id, "sourceOffset": hex(offset),
            "sourceByteCount": 96 * 8, "sourceSha256": expected_hash,
            "rows": 6, "columns": 16, "recordBytes": 8,
            "slots": slots,
            "holeSlots": [slot for slot, usage in enumerate(slots) if not usage],
            "bindingCount": len(lookup),
            "nonStandardUsageBytes": sorted(v for v in lookup if 0xA5 <= v <= 0xDF or v > 0xE7),
        })
        bindings.append(lookup)
    common = {usage: slot for usage, slot in bindings[0].items()
              if all(other.get(usage) == slot for other in bindings[1:])}
    return {
        "schemaVersion": 1,
        "model": "RZ09-0530", "usbIdentity": "1532:02C5",
        "component": "PI4110 keyboard MCU 1.04.00",
        "scope": "Static exact-model firmware source review; no installed-MCU attestation or physical validation",
        "decodedBytes": IMAGE_SIZE, "decodedSha256": IMAGE_SHA256,
        "hexSha256": HEX_SHA256, "carrierSha256": CARRIER_SHA256,
        "physicalValidation": False,
        "mapStatus": "SourceReviewedRegionalDescriptorMaps",
        "source": {
            "carrierUrl": "https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282025%29%20%7C%20RZ09-0530/PI4110-MCUv01.04.00.exe",
            "carrierBytes": CARRIER_SIZE,
            "extractedHexName": "PI4110_keyboard_FW_v1.04.00_20250918.hex",
            "trustBasis": "Exact carrier SHA256 and Razer USA Ltd. signature previously reviewed; this verifier checks hashes only",
        },
        "geometry": {
            "rows": 6, "columns": 16, "slotCount": 96,
            "slotMeaning": "Descriptor record index: row=index//16, column=index%16; record coordinate fields are not used as address authority",
            "extraLedRecords": "Separate descriptor pointer +28 and count +0D are outside this 96-slot keyboard map",
        },
        "selectorGroups": [
            {"layoutId": "map-afe3", "selectorValues": [0, 1, 5, 8, 9, 11]},
            {"layoutId": "map-b63b", "selectorValues": [12]},
            {"layoutId": "map-b313", "selectorValues": [2, 3, 4, 6, 7, 10],
             "otherByteValues": True},
        ],
        "selectorProvenance": {
            "function": "0x5d24", "switch": "0x5d98",
            "editionByteAddress": "0x10001870",
            "activeDescriptorPointerAddress": "0x1000012c",
            "countryNames": "Not inferred from edition values",
        },
        "variants": variants,
        "commonUsageToSlot": {str(usage): common[usage] for usage in sorted(common)},
        "commonBindingCount": len(common),
        "commonBindingPolicy": "When edition is unavailable, use only usage/index pairs identical across all three maps; never choose a guessed regional map",
        "firmwareUsagePolicy": "Reserved/proprietary firmware usage bytes remain separate from standard keyboard-page usages and are not automatically Windows input bindings",
        'commandAddressProof': {'customRowHandler': '0x84be',
         'stagedRgbBuffer': '0x10001955',
         'activeRgbBuffer': '0x10000d5a',
         'standardCustomActivation': '0x82b6; effect05 branch0x83c6',
         'frameCopy': '0x4a8c; copies0x120 RGB bytes without permutation',
         'stagedSourceCondition': '*0x100001b9 != 0 OR (*0x10000179 != 0 AND *0x10000188 != 0)',
         'alternateSource': '0x100012d6; flag semantics are not newly admitted by this map review',
         'sourceDefault': 'Application CRT initializesRAM100001B9 to1 fromROMCD7D; standard staged source is selected without a mode write',
         'laterInitialization': '0x5f4c stores capability record100001B8+4 and+2 only; its+1 standard-frame-source flag remains1',
         'otherSourceFlags': '0x10000179 belongs to extended-frame control0F/10 with getter0F/90; USB/session state0x10000188 is not a prerequisite while the standard-source flag is1',
         'renderer': '0x682c; RGBoffset=row*48+column*3 and descriptorOffset=row*128+column*8',
         'boundary': 'Address ordering is source reviewed; no new flag writes or physical frame-output '
             'confirmation are established'},
        'editionGetter': {'commandClass': '00',
         'commandId': '86',
         'requestDataBytes': 2,
         'responseByte0': 'Same stored edition byte used by selector; RAM0x10001870',
         'responseByte1': 'Adjacent stored byte RAM0x10001871; not assigned a new semantic meaning here',
         'proof': '0x7030 loads settings pointer;0x7034 loads+4;0x7036 stores local;0x71b0/71b2 returns '
          'payload0;0x71b4/71b6 returns+5 at payload1',
         'runtimeUse': 'Optional; the common intersection requires no getter or guessed edition'},
        'usbInputDescriptors': [{'interface': 0,
          'reportOffset': '0xc2cf',
          'reportBytes': 61,
          'topLevelCollections': [{'usagePage': 1, 'usage': 6, 'reportIds': []}],
          'reportSha256': 'a638a07d99bb58fa0734d44e7b20ac7327bd0b92aec401420ceafbae0c552915'},
         {'interface': 1,
          'reportOffset': '0xc230',
          'reportBytes': 159,
          'topLevelCollections': [{'usagePage': 1, 'usage': 6, 'reportIds': [1]},
                          {'usagePage': 12, 'usage': 1, 'reportIds': [2]},
                          {'usagePage': 1, 'usage': 128, 'reportIds': [3]},
                          {'usagePage': 1, 'usage': 0, 'reportIds': [4]},
                          {'usagePage': 1, 'usage': 0, 'reportIds': [5]}],
          'reportSha256': '1a8ded231a2df9765104bf1d0de3449f35676de229dab0300892fda05735c40a'},
         {'interface': 2,
          'reportOffset': '0xc1d2',
          'reportBytes': 94,
          'topLevelCollections': [{'usagePage': 1, 'usage': 2, 'reportIds': []}],
          'reportSha256': '7617239978ffd0d1a82b5d22ee7038ccc51881e019af72cb4d78185db54ad9b4'},
         {'interface': 3,
          'reportOffset': '0xc30c',
          'reportBytes': 327,
          'topLevelCollections': [{'usagePage': 89, 'usage': 1, 'reportIds': [1, 2, 3, 4, 5, 6]}],
          'reportSha256': '6ae88aeb15120171f2112d116c2bc987a173368b6197dbce3b87bcdbde9c9d8d'}],
        'inputAdmissionBoundary': 'Windows input binding restricts exact verified host model, VID/PID, reviewed interface/collection and typed RawInput keyboard packets. Exclude interface02 mouse, interface03 vendor page59 and interface01 non-keyboard collections. The service retains fresh per-write hardware admission.',
        "instructionProofs": PROOFS,
        "assertions": "Passed",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path, help="Pinned PI4110 Intel HEX or decoded binary")
    parser.add_argument("--carrier", type=Path, help="Optional independently pinned original carrier; never executed")
    parser.add_argument("--output", type=Path, help="New JSON report; refuses to overwrite")
    args = parser.parse_args()
    try:
        raw = args.image.read_bytes()
        if args.image.suffix.lower() in (".hex", ".ihex", ".ihx"):
            require(sha(raw) == HEX_SHA256, "Intel HEX SHA256 mismatch")
            decoded = decode_intel_hex(raw)
            require(decoded.base_address == 0, "Intel HEX image base mismatch")
            image = decoded.data
        else:
            image = raw
        if args.carrier:
            carrier = args.carrier.read_bytes()
            require(len(carrier) == CARRIER_SIZE and sha(carrier) == CARRIER_SHA256,
                    "Original carrier size or SHA256 mismatch")
        result = verify_image(image)
        rendered = json.dumps(result, indent=2) + "\n"
        if args.output:
            with args.output.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(rendered)
        else:
            print(rendered, end="")
    except (OSError, ValueError) as error:
        print(f"Verification failed: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
