---
title: Rule out the legacy mailbox scratch marker for third fan PWM
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-bram3-readback-review.md
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

The direct Super I/O reader still needs an exact-host fixed-decode observation. The follow-up now records the narrow PCI config and DMI capture using setpci without register assignment or EC traffic. A set C1LE bit establishes the fixed forwarding state for the sampled boot; it does not prove EC ownership or PWM address binding.

## Tried and ruled out

The four exact legacy command 10 bodies have fixed RAM reads; tracing both branch outcomes finds no PWM3 or descriptor guard. Command 12 calls the same 80700-byte-window helper in all four. Slot 18 survives command 90 cleanup and is cleared by ordinary and delayed writers, but the storage code copies it into a record, so it is not private scratch. An ordinary callback also clears address-high 13 after clearing slot 18. Pausing between those stores lets it erase a freshly staged address while the new marker survives. The pinned 001808 byte is zero in all four images.

## Watch out

This is an exact-image branch and byte check with a conditional interleaving. Do not treat the marker as an ownership claim, invoke other legacy commands as a substitute for a reader, or present a wrong-address zero as a captured hardware failure. The capture must be on a matching host and requires no boot change.
