---
title: Blade fan Stop legacy fence and exact I2C endpoint binding
author: Codex
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
  - docs/fan-stop-model-coverage.md
---

## Decided
For the four Blade 17/18 images, legacy command 11 with the fixed active-state index E3 publishes result32 before clearing command12. Scheduled event 6 and primary event 13 share a main loop. A fresh legacy completion after the D0 copy can order the specific D0 tail under serialized callbacks. It leaves the later empty-event hazard for primary Auto.

## Tried and ruled out
An FF payload sentinel excludes the status-only false zero in the bounded model. This depends on intact request and payload fields. The alternate command90 reader still shares its raw descriptor with ordinary firmware. The separate I2C transport now binds both ends on all four models. EC slave58 matches exact keyboard MCU B0, and ordinary mode SET reaches selector1A. Shared cached GET82 cannot confirm it. Gated immediate reads use the same bus and have a separate read path.

## Watch out
I2C bank0 maps selector and override, while register0F can select an out-of-range bank. Bank validity and exclusive ownership are explicit prerequisites to further design. Complete handback also needs restoration readback and guardian lifetime. RZ09-0301's separate current-speed cache narrows its missing evidence to the receiving EC. OL510 ME packages establish a board ID without supplying an EC image.
