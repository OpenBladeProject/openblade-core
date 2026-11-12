#!/usr/bin/env python3
"""Verify exact-model firmware evidence for four-byte lighting effect readback.

Supply a locally acquired pinned keyboard MCU HEX or decoded binary. The
optional carrier is checked by exact size/hash, never loaded or executed.
This verifier performs no hardware or driver access. Signer validation and
source acquisition are separate provenance steps. Output omits firmware bytes.
No assertion about installed firmware versions or physical behavior is made.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path

PROFILES = [{'result': {'model': 'RZ09-0530',
             'usbIdentity': '1532:02C5',
             'component': 'PI4110 keyboard MCU 1.04.00',
             'carrierSha256': 'aed7039be5cc4ec3f166a2728b0c1b90839bae72c8a04150056654f64f20581f',
             'hexSha256': '4787f3c382d57c165dd2de099147b678070d0251e0febb5b6cc5a7902048bcd3',
             'decodedBytes': 52612,
             'decodedSha256': '1ac3f05486d0bca5fbbb1a4a52b874943ad491a00afd298b8d6b857a49d98f11',
             'scope': 'Static source evidence only; no live firmware attestation, physical readback or '
                      'hardware validation',
             'semantics': {'get': '0F/82 selector1,5 selects persistent keyboard state; writes mode, '
                                  'direction and speed at payload2,3,4 while retaining request DataSize',
                           'getDirectionForm': 'DataSize4 payload01 05 00 00 reveals direction within '
                                               'declared response; all90 transport bytes are transferred',
                           'getSpeedForm': 'DataSize5 also includes speed; Wave SET defaults stored speed '
                                           'to40, so expect nonzero28',
                           'set': '0F/02 Wave consumes payload3 unconditionally; omission is not '
                                  'preserve-current-direction semantics',
                           'shortSet': 'A zero-padded three-byte SET supplies wire direction0 from padding; '
                                       'do not use omission as unknown direction',
                           'preserveSet': 'Retain reviewed four-byte01 05 04 01 destination; no speed byte '
                                          'required by this image',
                           'admission': 'No DataSize comparison occurs along the reviewed fixed90-byte '
                                        'receive, class dispatch and Wave GET/SET paths',
                           'alternateDirectionReadback': 'Wire2 has an explicit cyclic phase branch opposite '
                                                         'to admittedwire1. Treat it as a known alternate '
                                                         'read-only baseline; this evidence adds no write2 '
                                                         'or physical orientation claim. Other raw values '
                                                         'remain outside the bounded application admission.'},
             'proofSites': [{'offset': '0x27CA',
                             'byteCount': 18,
                             'meaning': 'USB SET_REPORT copies all 90 bytes into frame 0x10001740; no '
                                        'payload DataSize test'},
                            {'offset': '0x26DC',
                             'byteCount': 16,
                             'meaning': 'USB GET_REPORT copies all 90 bytes from response frame'},
                            {'offset': '0x8FCE',
                             'byteCount': 42,
                             'meaning': 'Class dispatch reads frame byte6 and selects class0F'},
                            {'offset': '0x9044',
                             'byteCount': 6,
                             'meaning': 'Class0F dispatch calls handler0x8678'},
                            {'offset': '0x8FFC',
                             'byteCount': 28,
                             'meaning': 'Response path XORs bytes2 through87 and sets successful status; '
                                        'preserves DataSize'},
                            {'offset': '0x8678',
                             'byteCount': 92,
                             'meaning': 'Effect handler reads frame command7 and selectors8/9; no header '
                                        'DataSize read before GET/SET dispatch'},
                            {'offset': '0x876E',
                             'byteCount': 42,
                             'meaning': 'GET82 validates LED, clears payload2..11, normalizes storage, '
                                        'selects state, reads state effect+5'},
                            {'offset': '0x87A4',
                             'byteCount': 18,
                             'meaning': 'GET effect compact switch and inline table'},
                            {'offset': '0x8818',
                             'byteCount': 28,
                             'meaning': 'Wave GET stores effect4 at payload2, direction state+10 at '
                                        'payload3, speed at payload4; returns without DataSize change'},
                            {'offset': '0x88E8',
                             'byteCount': 36,
                             'meaning': 'SET selects stored/volatile state and switches on payload2'},
                            {'offset': '0x8AA2',
                             'byteCount': 58,
                             'meaning': 'Wave SET validates LED and compares actual payload3 with current '
                                        'direction'},
                            {'offset': '0x8AE8',
                             'byteCount': 36,
                             'meaning': 'Wave SET stores effect4 and actual payload3; hardcodes speed40; '
                                        'does not read speed argument'},
                            {'offset': '0x6248',
                             'byteCount': 52,
                             'meaning': 'State selector returns matching LED bank pointer'},
                            {'offset': '0x22A8',
                             'byteCount': 36,
                             'meaning': 'Unaligned little endian32-bit read/write helpers'},
                            {'offset': '0x46EC',
                             'byteCount': 26,
                             'meaning': 'Compact switch helper validates count and doubles destination '
                                        'offsets'},
                            {'offset': '0x87B6',
                             'byteCount': 98,
                             'meaning': 'Off, Breathing and Spectrum GET branches never write payload3 after '
                                        'common clearing; return fourth byte zero'},
                            {'offset': '0x31C8',
                             'byteCount': 48,
                             'meaning': 'Wave renderer dispatch indexes active-state effect byte5'},
                            {'offset': '0x3296',
                             'byteCount': 28,
                             'meaning': 'Wave case loads direction state+0A into argument1 and callsA780'},
                            {'offset': '0xA780',
                             'byteCount': 96,
                             'meaning': 'Wave consumer explicitly increments cyclic phase for wire2; '
                                        'decrements for other values including admittedwire1'}],
             'source': {'carrierUrl': 'https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282025%29%20%7C%20RZ09-0530/PI4110-MCUv01.04.00.exe',
                        'carrierBytes': 1439888,
                        'extractedHexName': 'PI4110_keyboard_FW_v1.04.00_20250918.hex',
                        'trustBasis': 'Exact package SHA256 and Razer USA Ltd. signature reviewed '
                                      'separately; verifier checks hashes only'}},
  'windows': [{'offset': '0x27CA',
               'bytes': '0d4a2b5c1354401cc0b25a28f9d314700c70',
               'meaning': 'USB SET_REPORT copies all 90 bytes into frame 0x10001740; no payload DataSize '
                          'test'},
              {'offset': '0x26DC',
               'bytes': '0a4900200a5c3254401cc0b25a28f9d3',
               'meaning': 'USB GET_REPORT copies all 90 bytes from response frame'},
              {'offset': '0x8FCE',
               'bytes': 'a0790a282ed008dc00281fd0022820d0032821d0072808d121e00d2825d00e2826d00f2827d0182828d0',
               'meaning': 'Class dispatch reads frame byte6 and selects class0F'},
              {'offset': '0x9044',
               'bytes': 'fff718fbd8e7',
               'meaning': 'Class0F dispatch calls handler0x8678'},
              {'offset': '0x8FFC',
               'bytes': '02200021225c401c5140c0b25828f9d3144840300176207801281bd0',
               'meaning': 'Response path XORs bytes2 through87 and sets successful status; preserves '
                          'DataSize'},
              {'offset': '0x8678',
               'bytes': 'feb5ff48fd4c84462646c57d083efd4831460278f379087a0027497a802b25d015dc102b7cd00ddcf74a0092f74a0192022b76d0f14a083a927a032b72d0042b33d1b9e3112b6ed0182bf9d1e1e3902b6ad006dc812b24d0822b4cd0',
               'meaning': 'Effect handler reads frame command7 and selectors8/9; no header DataSize read '
                          'before GET/SET dispatch'},
              {'offset': '0x876E',
               'bytes': '052901d0042925d100202118401cc0b28f700a28f9d320786178002800d00120fdf75bfd0546407905e0',
               'meaning': 'GET82 validates LED, clears payload2..11, normalizes storage, selects state, '
                          'reads state effect+5'},
              {'offset': '0x87A4',
               'bytes': '0300fbf7a1ff09060d15303745495b787c00',
               'meaning': 'GET effect compact switch and inline table'},
              {'offset': '0x8818',
               'bytes': '6078052801d00428f2d10420a070a87ae070a81df9f73cfd2071febd',
               'meaning': 'Wave GET stores effect4 at payload2, direction state+10 at payload3, speed at '
                          'payload4; returns without DataSize change'},
              {'offset': '0x88E8',
               'bytes': '02982946002800d00120fdf7a9fc0546a0780300fbf7f6fe0a0c2a5189d1f3f2f1f0ef29',
               'meaning': 'SET selects stored/volatile state and switches on payload2'},
              {'offset': '0x8AA2',
               'bytes': '6078052804d0010002d0042803d036e0f6490f7001e0f4494f706979042908d0002910d005290ed007290cd006290ad011e0e078a97a88420dd1',
               'meaning': 'Wave SET validates LED and compares actual payload3 with current direction'},
              {'offset': '0x8AE8',
               'bytes': '05280fd000280dd001210846fcf76efc04206871e078a872a91d2820f9f7dbfbef720be0',
               'meaning': 'Wave SET stores effect4 and actual payload3; hardcodes speed40; does not read '
                          'speed argument'},
              {'offset': '0x6248',
               'bytes': '30b5034600200b4c02469501655d8d420bd1002b06d0012b03d80749900140180f3030bd9001001930bd521cd2b2022aebd330bd',
               'meaning': 'State selector returns matching LED bank pointer'},
              {'offset': '0x22A8',
               'bytes': '8178c278037809041206114342781002184308437047020a08704a70020c8a70020eca70',
               'meaning': 'Unaligned little endian32-bit read/write helpers'},
              {'offset': '0x46EC',
               'bytes': '30b47446641e2578641cab4200d21d46635d5b00e31830bc1847',
               'meaning': 'Compact switch helper validates count and doubles destination offsets'},
              {'offset': '0x87B6',
               'bytes': '6078052801d0042800d1a770febd6078052801d00428f9d10120a0703de06078052801d00428f1d10220a070e87a607100200be041004218511912190b7b93714b7bd371897b401c1172c0b2e97a8142f0d8febd6078052801d00428f9d1032044e0',
               'meaning': 'Off, Breathing and Spectrum GET branches never write payload3 after common '
                          'clearing; return fourth byte zero'},
              {'offset': '0x31C8',
               'bytes': 'f0b505467e4940014618a8014031441885b0022d13d26079ff21213106287dd006dc030001f07efa061929324a536111',
               'meaning': 'Wave renderer dispatch indexes active-state effect byte5'},
              {'offset': '0x3296',
               'bytes': '2078052807d00428b2d1a17a4a4a284607f06bfaace7a17a484af8e7',
               'meaning': 'Wave case loads direction state+0A into argument1 and callsA780'},
              {'offset': '0xA780',
               'bytes': 'f7b50446564940014518a001403186b04018029017462846f7f786fd06460298801df7f781fd86427ed929460020f7f786fd02980278079802284a48015d06d000290bd0491e0155052a09d053e0491cc9b201551629f7d30021f4e71521f2e7',
               'meaning': 'Wave consumer explicitly increments cyclic phase for wire2; decrements for other '
                          'values including admittedwire1'}],
  'pointers': [{'offset': '0x8a74', 'value': '0x10001748'},
               {'offset': '0x9060', 'value': '0x10001740'},
               {'offset': '0x8a78', 'value': '0x1000193b'},
               {'offset': '0x627c', 'value': '0x100014f6'},
               {'offset': '0x6280', 'value': '0x1000189b'},
               {'offset': '0x33c8', 'value': '0x100014b6'},
               {'offset': '0xa8e0', 'value': '0x100014b6'}],
  'usbIdentityOffset': '0xc1c8',
  'usbIdentityBytes': '3215c502',
  'switches': [{'call': '0x87a6', 'index': 4, 'target': '0x8818'},
               {'call': '0x88fc', 'index': 4, 'target': '0x8aa2'},
               {'call': '0x31ec', 'index': 4, 'target': '0x3296'}]},
 {'result': {'model': 'RZ09-0482',
             'usbIdentity': '1532:029D',
             'component': 'PI490 keyboard MCU1.04.00',
             'decodedBytes': 59456,
             'decodedSha256': 'dc4d17d4e552922c7712225301bd31474d61e4b12908e00ffa02da4e2fccbe05',
             'hexSha256': '42adb76bcb84203237284ed652955e6cdc1ebfb86de4fedb4b780cdf364be54c',
             'carrierSha256': '9653d899701a889f3e533e8e4a6733869ca3b4bc5c66942b6512d3d848c40e51',
             'scope': 'Static source evidence only; existing reviewed Ghidra project exported read-only, '
                      'decoded image hash independently matched prior pinned review',
             'semantics': {'getDirectionForm': 'DataSize4 payload01 05 00 00 returns mode and actual stored '
                                               'MCU direction in declared response; no request-size '
                                               'dependency in traced path',
                           'set': 'Direction argument3 is read unconditionally; zero-padded omission is '
                                  'direction0, not preserve-current-direction',
                           'speed': 'Wave setter independently writes fixed speed40; payload4 unused',
                           'otherModels': 'Does not establish029F or02A0 applicability',
                           'alternateDirectionReadback': 'Wire2 has an explicit cyclic phase branch opposite '
                                                         'to admittedwire1. Treat it as a known alternate '
                                                         'read-only baseline; this evidence adds no write2 '
                                                         'or physical orientation claim. Other raw values '
                                                         'remain outside the bounded application admission.'},
             'proofSites': [{'offset': '0xC448',
                             'byteCount': 58,
                             'meaning': 'USB receive checks transfer length90 then copies90 bytes; no '
                                        'DataSize check'},
                            {'offset': '0xC3CE', 'byteCount': 40, 'meaning': 'USB response copies90 bytes'},
                            {'offset': '0xB74E',
                             'byteCount': 26,
                             'meaning': 'Class switch; class15 targetsB792'},
                            {'offset': '0xB792', 'byteCount': 6, 'meaning': 'Class0F calls95B0'},
                            {'offset': '0x95B0',
                             'byteCount': 56,
                             'meaning': 'Effect handler command dispatch; no DataSize read'},
                            {'offset': '0x96AC',
                             'byteCount': 74,
                             'meaning': 'GET82 clears payload2..11 and selects stored or volatile state; '
                                        'mode switch'},
                            {'offset': '0x979A',
                             'byteCount': 30,
                             'meaning': 'Wave getter returns mode4,direction+3,speed+4 without DataSize '
                                        'change'},
                            {'offset': '0x96F6',
                             'byteCount': 164,
                             'meaning': 'Other supported GET modes leave cleared payload3 zero'},
                            {'offset': '0x962C', 'byteCount': 2, 'meaning': 'GET return branch'},
                            {'offset': '0x9870', 'byteCount': 4, 'meaning': 'GET return branch'},
                            {'offset': '0xA264', 'byteCount': 2, 'meaning': 'GET final return branch'},
                            {'offset': '0xA292', 'byteCount': 4, 'meaning': 'GET final return'},
                            {'offset': '0x98E8',
                             'byteCount': 46,
                             'meaning': 'SET state selection and effect switch'},
                            {'offset': '0x9AE8', 'byteCount': 2, 'meaning': 'SET Wave branch'},
                            {'offset': '0x9B4A',
                             'byteCount': 142,
                             'meaning': 'Wave SET consumes payload3; hardcodes speed40'},
                            {'offset': '0xB7A2',
                             'byteCount': 48,
                             'meaning': 'Class response checksum/status updates preserve DataSize'},
                            {'offset': '0x8380',
                             'byteCount': 56,
                             'meaning': 'State selector reads matching LED bank without mutation'},
                            {'offset': '0xD0E0',
                             'byteCount': 50,
                             'meaning': 'Unaligned little endian32-bit read/write helpers'},
                            {'offset': '0x9E16',
                             'byteCount': 2,
                             'meaning': 'GET return connector: unconditional Thumb branch9E16->A264; no '
                                        'DataSize mutation'},
                            {'offset': '0x7F3E',
                             'byteCount': 52,
                             'meaning': 'Wave renderer dispatch indexes active-state effect byte5'},
                            {'offset': '0x802A',
                             'byteCount': 36,
                             'meaning': 'Wave case loads direction state+0A into argument1 and calls5C46'},
                            {'offset': '0x5C46',
                             'byteCount': 132,
                             'meaning': 'Wave consumer explicitly increments cyclic phase for wire2; '
                                        'decrements for other values including admittedwire1'}],
             'source': {'carrierUrl': 'https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-MCUv010400v2.exe',
                        'carrierBytes': 1330264,
                        'extractedHexName': 'PI490_keyboard_FW_v1.04.00_20230608.hex',
                        'trustBasis': 'Exact package SHA256 and Razer USA Ltd. signature reviewed '
                                      'separately; verifier checks hashes only'}},
  'windows': [{'offset': '0xC448',
               'bytes': '6720c2490880c049c8798979000208435a2812d1c248007800280ed1002405e0bc48005dba490855601cc4b25a2cf7db0120b7490870ba490870',
               'meaning': 'USB receive checks transfer length90 then copies90 bytes; no DataSize check'},
              {'offset': '0xC3CE',
               'bytes': '6720e04a1080de4ad0799279000210435a2809d1002105e0dc48405cdc4a5054481cc1b25a29f7db',
               'meaning': 'USB response copies90 bytes'},
              {'offset': '0xB74E',
               'bytes': '8079030001f02bfe1009210c0f212121122121152121181b1e21',
               'meaning': 'Class switch; class15 targetsB792'},
              {'offset': '0xB792', 'bytes': 'fdf70dff03e0', 'meaning': 'Class0F calls95B0'},
              {'offset': '0x95B0',
               'bytes': 'feb5f74c20460838c079802814d00adc022870d003286fd004286ed0112801d000f05bfee8e3812829d0822866d0842879d09128f4d1f3e3',
               'meaning': 'Effect handler command dispatch; no DataSize read'},
              {'offset': '0x96AC',
               'bytes': '6078052802d0607804286fd1002604e00021b01c2154701cc6b20a2ef8db61782278002a02d0012201e0eee300221046fef750fe05466879030003f061fe09060f1f4e5867788abac000',
               'meaning': 'GET82 clears payload2..11 and selects stored or volatile state; mode switch'},
              {'offset': '0x979A',
               'bytes': '6078052802d06078042807d10420a070a87ae070a81d03f096fc20715be0',
               'meaning': 'Wave getter returns mode4,direction+3,speed+4 without DataSize change'},
              {'offset': '0x96F6',
               'bytes': '6078052802d06078042801d10020a070b3e06078052802d06078042808d10120a0706071287ba071687be071a87b2072a3e06078052802d06078042827d10220a070e87a607100261ee07000311828460c30415c70003018801d21547000311828460c300818417870003018c01d21547000311828460c30081881787000301808302154701cc6b2e87ab042dddc74e06078052802d06078042801d10320a0706be06be0',
               'meaning': 'Other supported GET modes leave cleared payload3 zero'},
              {'offset': '0x962C', 'bytes': 'f3e3', 'meaning': 'GET return branch'},
              {'offset': '0x9870', 'bytes': '00bfdbe6', 'meaning': 'GET return branch'},
              {'offset': '0xA264', 'bytes': '15e0', 'meaning': 'GET final return branch'},
              {'offset': '0xA292', 'bytes': '00bffebd', 'meaning': 'GET final return'},
              {'offset': '0x98E8',
               'bytes': '61782278002a01d0012200e000221046fef742fd0546a078030003f053fd0a06327fd9f1f0efeeedec3060780528',
               'meaning': 'SET state selection and effect switch'},
              {'offset': '0x9AE8', 'bytes': '2fe0', 'meaning': 'SET Wave branch'},
              {'offset': '0x9B4A',
               'bytes': '6078052805d06078002802d06078042839d16078052802d06078002803d100208a49087002e00020884948706879042803d1e178a87a814228d06879002808d06879052805d06879072802d0687906280ed16078052802d06078002804d101210020fcf792fe03e001210846fcf78dfe04206871e178a972a91d282003f09cfa0021e97202e00520714908702ce1',
               'meaning': 'Wave SET consumes payload3; hardcodes speed40'},
              {'offset': '0xB7A2',
               'bytes': '0224002504e02648005d4540601cc4b2582cf8db23484030057640380078012802d102201f49087000201f49087070bd',
               'meaning': 'Class response checksum/status updates preserve DataSize'},
              {'offset': '0x8380',
               'bytes': '30b502460b460020002111e08c01f74d2c5d9c420ad1002a02d18c01601904e0012a02dc8d01f24c281903e04c1ce1b20229ebdb00bf30bd',
               'meaning': 'State selector reads matching LED bank without mutation'},
              {'offset': '0xD0E0',
               'bytes': '0378c2781946437812061b0219438378c0781b04194311430902090a000608437047020a08704a70020c8a70020eca707047',
               'meaning': 'Unaligned little endian32-bit read/write helpers'},
              {'offset': '0x9E16',
               'bytes': '25e2',
               'meaning': 'GET return connector: unconditional Thumb branch9E16->A264; no DataSize mutation'},
              {'offset': '0x7F3E',
               'bytes': 'f1b582b002984001fe494018019002988001fc4944180298022800dbfebd6079062875d006dc030005f021fa0611263056607775',
               'meaning': 'Wave renderer dispatch indexes active-state effect byte5'},
              {'offset': '0x802A',
               'bytes': '2078052805d1a17ac54a0298fdf706fe07e02078042804d1a17ac14a0298fdf7fdfdace0',
               'meaning': 'Wave case loads direction state+0A into argument1 and calls5C46'},
              {'offset': '0x5C46',
               'bytes': 'f7b584b005466801bb4940180290a801b84940180190029807f03ffa07460198801d07f03afa87427dd90020029907f045fa01980078052802d11620039001e016200390059802280cd1bf48405d401cc1b2bd4841550398814211db0020ba4948550de0b948405d002804d10398401eb649485504e0b448405d401eb349485501980078',
               'meaning': 'Wave consumer explicitly increments cyclic phase for wire2; decrements for other '
                          'values including admittedwire1'}],
  'pointers': [{'offset': '0x9990', 'value': '0x100015d0'},
               {'offset': '0xb844', 'value': '0x100015c8'},
               {'offset': '0xc758', 'value': '0x100015c8'},
               {'offset': '0x8344', 'value': '0x1000137e'},
               {'offset': '0x5f38', 'value': '0x1000137e'}],
  'usbIdentityOffset': '0xe3d4',
  'usbIdentityBytes': '32159d02',
  'switches': [{'call': '0xb752', 'index': 15, 'target': '0xb792'},
               {'call': '0x96e6', 'index': 4, 'target': '0x979a'},
               {'call': '0x9902', 'index': 4, 'target': '0x9ae8'},
               {'call': '0x7f66', 'index': 4, 'target': '0x802a'}]}]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def verify(image_path: Path, carrier_path: Path | None = None) -> dict:
    source = image_path.read_bytes()
    source_hash = hashlib.sha256(source).hexdigest()
    matches = [profile for profile in PROFILES if source_hash in (
        profile["result"]["decodedSha256"], profile["result"]["hexSha256"])]
    require(len(matches) == 1, "Input does not match an exact reviewed MCU image or HEX")
    profile = matches[0]
    result = profile["result"]
    if source_hash == result["hexSha256"]:
        from blade_firmware.formats import decode_intel_hex
        image = decode_intel_hex(source)
        require(image.base_address == 0, "HEX base address must be zero")
        data = image.data
    else:
        data = source
    require(len(data) == result["decodedBytes"], "Decoded image size mismatch")
    require(hashlib.sha256(data).hexdigest() == result["decodedSha256"], "Decoded image hash mismatch")
    if carrier_path is not None:
        carrier = carrier_path.read_bytes()
        require(len(carrier) == result["source"]["carrierBytes"], "Carrier size mismatch")
        require(hashlib.sha256(carrier).hexdigest() == result["carrierSha256"], "Carrier hash mismatch")
    offset = int(profile["usbIdentityOffset"], 16)
    require(data[offset:offset + 4] == bytes.fromhex(profile["usbIdentityBytes"]), "Exact USB ID mismatch")
    for pointer in profile["pointers"]:
        offset, value = int(pointer["offset"], 16), int(pointer["value"], 16)
        require(struct.unpack_from("<I", data, offset)[0] == value, f"Pointer mismatch at {offset:#x}")
    for window in profile["windows"]:
        offset, expected = int(window["offset"], 16), bytes.fromhex(window["bytes"])
        require(data[offset:offset + len(expected)] == expected, f"Instruction pin mismatch at {offset:#x}")
    for switch in profile["switches"]:
        call, index = int(switch["call"], 16), switch["index"]
        require(index < data[call + 4], "Switch index is out of range")
        target = (call + 5 + 2 * data[call + 5 + index]) & ~1
        require(target == int(switch["target"], 16), "Switch destination mismatch")
    return {**result, "assertions": "Passed", "switches": profile["switches"]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--carrier", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    text = json.dumps(verify(args.image, args.carrier), indent=2) + "\n"
    if args.output:
        args.output.write_text(text, encoding="utf-8")
    print(text, end="")


if __name__ == "__main__":
    main()
