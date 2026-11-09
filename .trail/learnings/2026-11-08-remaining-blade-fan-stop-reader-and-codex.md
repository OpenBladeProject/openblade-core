---
title: Remaining Blade fan Stop reader and host-interface limits
author: Codex
date: 2026-11-08
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-it8320-trigger-investigation.md
  - docs/fan-stop-bram3-readback-review.md
  - docs/fan-stop-pi490-mcu-bridge-review.md
  - docs/fan-stop-missing-model-host-interfaces.md
---

## Decided
For the four three-channel DI780/KI890/KI8100/KI8110 images, keep primary completion-data validity separate from restoration availability. Arbitrary coalescing notifications did not create false successes in the bounded model (386 states, 14 fence acceptances), but an empty callback can erase a later Auto publication. A separate success-status writer changes that result. Applicable trigger-register semantics and computed-writer exclusion remain open. ITE8320EC is a firmware-family string, not an exact silicon binding.

## Tried and ruled out
Exact firmware SIO tables now establish302/303 on all four models. The legacy reader has a single final clear, but scheduled callbacks reuse its address bytes; a conditional schedule reads001808=00 instead ofF01808. Reachability under independently clean FE ownership is unproved, and observed FE does not certify callback drainage. Six BIOS host-interface traces found generic EC transports, disabled templates or missing OEM fan hooks. PI490 MCU1.04 forwards ordinary fan/mode bytes but rejects named0D/0D and0D/8D duty commands; its mode setter has no SO6120-style local<=7 check. These findings admit no new Stop model.

## Watch out
Resolved jral destinations must be traversed together with return fallthrough; overwriting either understated IRQ coverage. Direct-store inventories and representative proofs are not complete alias exclusion. Shared MCU caches are neither guaranteed request echoes nor independently fresh physical readings. Do not execute carriers, infer EC identity from MCU/BIOS versions, copy another model's semantics or treat static findings as physical validation. Broad trail check lexical matches with older stop-test learnings do not override these unchanged boundaries.
