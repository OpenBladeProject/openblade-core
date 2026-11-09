---
title: SO6120 application-state entry gates do not establish transfer ownership
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-mcu-transfer-ownership.md
  - tools/firmware/profiles/so6120-mcu-1.07.json
---

## Decided
The exact SO6120 MCU 1.07 application byte 100001E8 does not supply a reviewed ownership lease. Three explicit stores (4102 reset, 5E78 initialization, 72C6 host setter) and decoded startup initialization are recovered. SET accepts the full byte before comparing 1/5/2; state 1 resets, state 2 and entry to 3 or 3-to-0 affect auxiliary fields. GET is a sampled byte, so replaying the original byte is not exact restoration.

## Tried and ruled out
For one unchanged state snapshot, main parking entry needs USB 1000014E bit 3 set and state neither 3 nor 2; timer generic dispatch needs state 3 or that bit clear. No overlap in 512 combinations. This closes the unchanged-state parking-entry strategy. Normal peripheral and thermal transfers precede later main-loop gates and retain paths without the state check.

## Watch out
Parking retry at 4550..4556 does not resample 1E8: entry predicates are not continuously enforced invariants and do not rule out asynchronous transitions. Transition effects involving 3 precede the state store; comparisons to 1/5/2 follow it. Alias scan coverage is bounded (1146 seeds, three caps); computed, interprocedural, bulk and unrecovered writes are not excluded. No hardware observation, live transport, other-model support or write authority follows. Keep firmware bytes and private listings ignored; use docs/so6120-mcu-transfer-ownership.md and the exact-hash explorer profile for sanitized evidence.
