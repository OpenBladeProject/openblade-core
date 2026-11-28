---
title: Distinguish a complete fan mismatch from unreadable confirmation
author: Jack
date: 2026-11-10
branch: feat/rust-ordinary-fan-parity
touches:
  - tools/openblade-windows-host/src/fan_runtime.rs
  - tools/openblade-windows-host/src/fan_runtime/device.rs
---

## Decided

For a completed scripted fan recipe, a fresh complete disagreement is drift. Preserve the observed topology, retire ownership and leave the readback gate clear. Keep the gate for possible submission or unavailable confirmation. A later topology read never proves exact manual intent.

## Tried and ruled out

Do not publish a new status revision for identical state. Retire an unconsumed scripted fault before the read-only phase so it cannot leak into the next explicit request.

## Watch out

Match the two-field managed fan override and keep exact setter echo validation. The adapter-only replay keeps its scope when power changes. Managed controller loss invalidates the authority epoch, so simulated ownership must not survive a competing-controller interruption.
