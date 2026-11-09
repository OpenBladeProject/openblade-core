---
title: SO6120 mailbox completion lacks transaction ownership through the native stack
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-host-mailbox-contract.md
  - docs/so6120-host-mailbox-alternatives.md
  - tools/firmware/profiles/so6120-ec-1.04.json
---

## Decided
For an isolated SO6120 EC1.04 mapped A0/A1 request with stable fields and no lifecycle interruption, command clear12D8C follows handler return. Status80 precedes processing and survives zero count or skipped read-only writes. This is handler ordering, not owner/request identity or applied fan confirmation. New docs/so6120-host-mailbox-contract.md pins updater, EC, bundled x64 DLL and embedded driver evidence.

## Tried and ruled out
The updater's Razer Update Tool mutex is an application-instance mechanism, not per-mailbox acquisition. The generic wrapper checks only the last chunk's status, copies reads before checking it, and can return stale success without submitting invalid arguments. Actual direct use is two sequential one-byte Qevent writes with ignored results; no actual concurrent caller or collision is established. EC pending8000D is one flag; count/index/data stay live during mapped loops and command clear is unconditional. Native Out32/Inp32 perform individual IOCTLs and hide failure outcomes. The exact embedded x64 driver creates a nonexclusive device, CREATE returns success without owner/share checks, and byte-I/O paths have no cross-call ownership. Share-zero CreateFile arguments do not establish that missing contract.

## Watch out
Keep EC offsets, DLL RVAs and driver RVAs distinct (embedded driver preferred base10000). The driver was analyzed as data from the original DLL resource in memory, never loaded into Windows. The kernel conclusion covers its create and byte-I/O paths, not live Windows access policy, installed binaries, all drivers or other models. A bounded host wait does not cancel a submitted request. Preserve stop-on-ambiguity, independent mapped readback, exact restoration and separate host-loss cooling recovery; this audit admits no new live transport or raw-port dependency.
