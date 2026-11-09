---
title: SO6120 secondary transfer bounds do not reserve fan ownership
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-secondary-mailbox-contract.md
  - docs/so6120-acpi-vendor-transport.md
  - docs/so6120-host-mailbox-contract.md
---

## Decided
The BIOS F0/F1/F2 secondary bulk path is now traced into the pinned SO6120 EC1.04 image. It reads persistent records through the 89000 directory, not arbitrary EC RAM. Isolated stable records have at most32 chunk responses (one F0 plus at most31 F1), conditional on unchanged state and returning lower reads. This is a finite data-progress bound, not a deadline or ownership grant. See docs/so6120-secondary-mailbox-contract.md for exact addresses and scope.

## Tried and ruled out
F0 sets busy rather than rejecting an existing busy state, and a recognized selector replaces the active cursor/length. F1 mutates phase/timer/ready state before admission. F2 and eligible counter expiry clear transfer bookkeeping without fan handback. The timer gates one normal profile-change path, while the FE direct-duty branch bypasses that gate. The storage family really can affect fans: F3/F4/F5 program persistent thermal-table selectors20..27. Do not call the whole family non-fan, or present persistent erase/program operations as a volatile runtime PWM setter. BIOS E0/E1 data is a DTT data vault; E8 is threshold configuration; D0 identity remains unresolved.

## Watch out
Single-chunk F0 checks the stored checksum; the multi-chunk F1 path lacks a final checksum check. F2 leaves the selector, active cursor/length, progress counters and transfer window intact. The 0x78 counter is scheduler decrements, not verified milliseconds, and expiry waits for command zero. Lower reads briefly mask EC interrupts but do not reserve the transaction across host requests. Existing gated raw-byte branches are separate previously identified writer evidence. Check installed access-provider metadata before assuming a new driver is necessary, but a matching configured-file hash and running service do not establish loaded-memory identity, client exclusion or API admission. No device request, live flash read/write, vendor execution, service change or driver installation was performed.
