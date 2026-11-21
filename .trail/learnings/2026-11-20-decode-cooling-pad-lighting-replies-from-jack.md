---
title: Decode cooling-pad lighting replies from firmware 1.10.00
author: Jack
date: 2026-11-20
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 54, tokens: 536819 }
touches:
  - CHANGELOG.md
  - docs/cooling-pad.md
  - src/OpenBlade.Protocol/CoolingPadProtocol.cs
  - src/OpenBlade.Protocol/SourceBackedCoolingPadDevice.cs
  - tests/OpenBlade.Protocol.Tests/CoolingPadSourceProtocolTests.cs
---

## Decided
The RC21-0230 (`1532:0F43`) firmware 1.10.00 image is the authority for its lighting replies. Rebuild it from the signed updater: the SFX exe carries a ZIP at offset 129601, `DeviceUpdater.resources` holds 2049 Intel HEX `DevFWLineN` strings, and the image SHA-256 is 8C8EACF5.... It is Cortex-M0 Thumb (LPC11Uxx; SRAM 0x10000000). The class 0F handler is at 0x64E0, with the report at 0x100008B2 and args at +8. `0F/80` returns exactly [05,19,03,01,12] (one row at byte 3, 18 LEDs at byte 4). `0F/82` returns 12 bytes: [0, stored region id, effect, flags, rate, count, slot1, slot2]. `0F/02` (0x6E40) stores only the effect id for effect 8, and color slot 1 only for Static. Decode that layout and confirm only the stored fields.

## Tried and ruled out
The earlier decoder layout (LED count at byte 3, rows at byte 4; profile in effect byte 0; all-field readback) was never physically checked and failed every status read. REA's Ghidra provider on Windows accepts only PE; a small ARMv6-M disassembler in a scratch console app was enough. Effects 7 and 9 exist in the firmware but stay unadmitted.

## Watch out
A failing lighting decode blocks the whole pad status read, fan control included. Breathing colors are not stored by `0F/02`; the renderer was not traced, so Breathing color and exact restoration of a non-Static baseline after a Static change remain untested. Install test builds through the broker with key OpenBlade.LocalDevDeployment.v4; v2 is stale.
