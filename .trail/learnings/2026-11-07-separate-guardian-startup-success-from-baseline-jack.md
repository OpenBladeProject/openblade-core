---
title: Separate guardian startup success from baseline headroom refusal
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-guardian-baseline-refusal.md
---

## Decided
The corrected SO6120 guardian startup can admit its reviewed caller/private-job scope while an ambient Windows job remains. The 2026-11-07 attended USB-C Balanced run passed startup but refused preparation because captured PWM97/90 exceeded the fixed91/91 target on the first fan at cycle183. This validates startup only; caller-loss Auto recovery remains untested.

## Tried and ruled out
Offline verification of the complete55-frame journal found13 successful A0 reads, no A1 or setting intent, and a normal Automatic baseline. Native closure and original caller/owner/controller exits were recorded. No hardware command was sent during reconciliation, and no automatic retry or target change followed.

## Watch out
A startup sidecar with acquisitionStartedAtRecord=false is historical: this run later acquired hardware. Do not classify it as a pre-acquisition refusal. The generic incomplete/knownNoSettingWrite=false receipt is conservative; preserve it and use the full hash-verified journal plus source path for the narrower no-setting-write conclusion. Keep the consumed marker intact until separately reviewed disposition. Do not claim Auto restoration or caller-loss validation from an unchanged Auto baseline.
