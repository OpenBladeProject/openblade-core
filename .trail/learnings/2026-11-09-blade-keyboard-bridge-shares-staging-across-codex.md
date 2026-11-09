---
title: Blade keyboard bridge shares staging across interrupt and foreground
author: Codex
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
  - docs/fan-stop-model-coverage.md
---

## Decided

The four pinned Blade 17/18 keyboard images call their HID dispatcher from the application timer vector and the thermal worker from the foreground loop. Both paths stage into the same I2C frame and length fields. A firmware-side exclusion argument must cover this pair even when a host serializes requests.

## Tried and ruled out

Two direct call sites reach each bus engine. The older forwarding wrappers use fixed indexes 00/01/02/FD/FE. The write terminal result is stored after requesting STOP. The engine returns without an idle observation. A model starting with a quiet primary mailbox can lose Auto with just one residual notification.

## Watch out

The shared-buffer overlap depends on interrupt scheduling or masking. Treat it as a source-backed window, not a physical reproduction. An I2C timeout leaves submission and bus state ambiguous. No public receiver bytes were acquired for CH530 or the other six.
