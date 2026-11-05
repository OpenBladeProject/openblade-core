---
title: SO6120 IPF completion and BIOS secondary mailbox boundaries
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-acpi-vendor-transport.md
  - docs/so6120-host-mailbox-contract.md
---

## Decided
The exact RZ09-0581 BIOS 4.01 and installed Intel IPF package add transport evidence, not direct fan authority. IPF serializes individual primitives but SET _FSL only reports ACPI completion; there is no established cross-request ownership lease or independent applied-state confirmation. PnP confirms TFN1/TFN2 presence/binding. The installed fan compact catalog maps 84/85/86/87 to _FIF/_FPS/_FST/_FSL. See docs/so6120-acpi-vendor-transport.md for hashes and addresses.

## Tried and ruled out
The existing signed BIOS carrier statically decodes to the exact local BIOS payload. Its AMI PFAT blocks must be reconstructed before UEFI decompression: feeding the wrapper directly produced apparent LZMA corruption at an inserted block signature. Independent reconstruction verified all 20 components/29 blocks. The main volume yields 67 checksum-valid ACPI tables; checked fan/mailbox methods match the Windows copies, and UPF2/RCFS/DUMY implementations remain absent from those recovered tables. Native ACPISMM SMI E1 command3 registers PanelColorProfileSmm and writes OemSetupVar=1; it is not a fan or mailbox API.

## Watch out
The installed full Intel FPC layout differs from public source; only the independently checked DV and compact primitive records are decoded. Presence, compiled ACL, method names and primitive completion do not prove live selection, actuation or ownership. BIOS table constants are patched at boot; identical method bytes do not imply identical live values. Recovered main-volume AML is not the complete runtime namespace. RazerAcpiDxe uses a distinct 302/303 plus FE0B8000 boot bulk path: F0 start, F1 continuation without a total loop bound, F2 cleanup whose expiry is ignored. Its E0/E1/E8/D0 selectors are not SMI E1 or primary mailbox D0. The exact EC-side secondary dispatcher is the next useful source lead; do not substitute its commands or treat startup bit15.5 as an OS owner token. No hardware calls, AML evaluation, vendor execution, installation or firmware writes were performed. Binaries and raw disassemblies stay ignored; this does not enable another model or a live backend.
