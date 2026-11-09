---
title: SO6120 ACPI and private Razer interfaces do not establish mailbox ownership
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-host-mailbox-contract.md
  - docs/so6120-acpi-vendor-transport.md
---

## Decided
The current RZ09-0581 BIOS 4.01 DSDT defines a second host mailbox client, ECRR via BMRD/BMWD at ports 300/301. It attempts ECMT acquisition but ignores timeout, uses NoLock fields and has a bounded poll followed by status-only validation. Defined AML is not observed runtime contention. No TBCC caller was found in the collected DSDT plus 34 cached SSDTs. See docs/so6120-acpi-vendor-transport.md for pinned inputs and byte offsets.

## Tried and ruled out
The exact installed 02E0 driver private controls 88883010/14 forward separate feature SET/GET requests B0191/92; common controls 88883040/44/48/4C manipulate a collection. Neither creates an owned EC transaction. ACPI fan methods exist, but their UPF2/RCFS/DUMY actuator definitions are missing from the collected tables; _FSL caches requested level without checking completion. PWMV is a display-related byte, not fan control. RECR/RECW reach only the fixed 80700 extension page, not arbitrary direct-duty fields.

## Watch out
ACPI D0/D1 raw-address status80 follows copy, unlike mapped A0/A1 early80; oversized initial counts still return80/command0 without copying. No command was submitted. Windows firmware enumeration omitted DSDT; an explicit GetSystemFirmwareTable DSDT request succeeded. Repeated SSDTs require another source; the read-only registry cache had34 entries/33 unique payloads. Combined iasl namespace disassembly failed on duplicates; scoped local methods were independently checked against mixed AML. Cache evidence does not prove runtime namespace or active hardware gates. No kernel driver, vendor execution, AML evaluation or hardware write was used. Missing ownership, request identity and cooling recovery remain separate gates.
