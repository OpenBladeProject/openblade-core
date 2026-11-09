---
title: Verify Blade fan reader claims after Claude review
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
  - docs/fan-stop-bram3-readback-review.md
---

## Decided
Claude Opus 5.5 high-effort review on 69b4af1b returned ten documentation findings. The native review inferred a docs-only scope after shell diff access was unavailable, so it did not independently re-review the production changes in the full PR. Reconcile each claim against pinned firmware, exact disassembly and original donor posts before editing.

## Tried and ruled out
The address pairs 9484/9468 are separate paths to the same RAM window. Legacy opcode 11 skips the opcode 12 call before joining its result store. The receive counter reaches 0x100, the 0529 IRQ enable and timer start offsets are in the documented order, and the two repair threads really use different FCS/FSC spellings. The CH551 donor post says RZ09-0330 even though its containing thread is titled RZ09-0328.

## Watch out
Programmed PCI decode does not prove DMI forwarding on 0529. A nonzero PWM payload can pass the range check without proving that the separate legacy fence observed fresh state outside FE. Keep both gaps explicit. The clarification changes no runtime behavior or admission.
