---
title: Recover exact Blade 17 and 18 BIOS host interfaces from PFAT
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

For RZ09-0423 BIOS 2.01, RZ09-0484 BIOS 2.04/2.09, RZ09-0509 BIOS 1.06, and RZ09-0529 BIOS 4.01, extract the signed updater as data, parse the AMI PFAT component manifest, and join the data spans for FV_MAIN_WRAPPER. The guided LZMA section at 310D8 (A00A8 on 0529) can be independently decompressed to its exact declared size. The signed inputs and recovered ACPI table hashes are pinned in docs/fan-stop-reader-and-ec-followup.md.

All four DSDTs name the fixed 2E/2F Super I/O decode bit C1LE at PCI D31:F0, bit 12 of the word at 82h. Their recovered AML has the RRIO toggle but no caller. A narrow config-word read on an exact host could establish the observed fixed decode without an EC command. This would still leave direct PWM address binding and selector ownership to prove.

## Tried and ruled out

Decoding compressed bytes from physical offsets inside the unassembled PFAT file failed when it reached BIOS Guard signatures and headers. The reconstructed data-only stream decompresses cleanly. The ACPI RECR method, despite runtime callers, maps to the EC RAM window at 80700h on all four pinned EC images. The third fan PWM at F01808h and its feedback copy at 80536h are outside that window.

## Watch out

The three Blade 18 BIOS corpora have an uncalled TBCC method that can submit D0 through ECRR on 300/301. The Blade 17 exposes the same mailbox fields and generic helpers, with no TBCC or ECRR in its reviewed DSDT. This bounds the recovered AML only. SMM, drivers and other runtime clients remain separate ownership questions. The A1 completion proof and independent Auto restoration are separate from the reader evidence.
