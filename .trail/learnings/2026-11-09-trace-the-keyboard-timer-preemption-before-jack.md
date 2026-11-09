---
title: Trace the keyboard timer preemption before Fan Stop writes
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided
Keep the immediate keyboard bridge outside live three-channel Fan Stop. The pinned four MCU images arm timer IRQ 16 and run CPSIE before the foreground thermal worker. A later immediate HID read can preempt an already queued selector write after staging and overwrite its global frame and lengths.

## Tried and ruled out
Serializing host HID calls alone does not serialize the MCU. A prior selector request can return before foreground transmission. The read helper does not restore staging after its own transfer.

## Watch out
The scheduling counterexample requires a pending gated read and the interrupt window. It is static evidence, not a captured failure. The direct I2EC reader still needs an exact installed address-map and host-decode proof. Normalize doubled carriage returns before validating GitHub PR version fields, and test the exact description retrieved from GitHub.
