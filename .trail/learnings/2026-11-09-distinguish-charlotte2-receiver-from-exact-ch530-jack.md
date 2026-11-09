---
title: Distinguish Charlotte2 receiver from exact CH530 evidence
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

The signed Charlotte2 EC 1.03 payload for RZ09-0288 is a positive control for a 256 KiB ITE8390EC NDS32 receiver. Its receive-address setup writes seven-bit 58, matching the B0 write-address byte seen in RZ09-0301 keyboard firmware. Its bank-zero map at 228DC stages B8/B9 to 80394/80395 and leaves 9C in read-only scratch. The FE path still writes actual PWM3, so matching bus address and selector do not establish the exact model's writable channels.

## Tried and ruled out

The positive-control marker and reset vectors did not find a receiver in the retained DA530, CH530, DA550 or OL510 packages. The additional Razer-signed C3/D3 12.0.38.1430 carriers both select the same ME-only image. DA550's Razer Update Tool folder is the standard carrier extraction path. KI890/KI8100 ACPI fan status uses the inactive H_EC template. KI8110 depends on an unresolved GFNS hook and defines two fan IDs.

## Watch out

On ITE SMBus, F01C48 is the seven-bit receive-address register and F01C4C controls slave interrupts. Do not confuse host ACPI timer port 1808 with the EC's internal PWM address F01808. A late BIOS scan without a direct decode write cannot rule out an indirect write or a boot-script entry. ITE's Linux DLB4 utility lacks a dump-only CLI, and Chromium's read path changes target debug state. Prefer an attributable original exact-board dump.
