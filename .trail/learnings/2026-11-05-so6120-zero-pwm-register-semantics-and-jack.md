---
title: SO6120 zero PWM register semantics and nested transaction status
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-direct-duty-safety.md
  - docs/so6120-mcu107-ec-fan-bridge.md
  - docs/so6120-ec-register-semantics.md
---

## Decided
SO6120 EC 1.04 uses raw zero as an ordinary PWM-register value in its normal fan controller and lifecycle paths. FE copies the same raw bytes without percent conversion; setup sets the normal cycle ceiling to183, and a floor helper converts percent*cycle/100. This does not prove mechanical fan stop or a safe register experiment. Continue the requested register investigation; the separate live Silent Automatic comparison stays paused. Exact evidence is in docs/so6120-ec-register-semantics.md.

## Tried and ruled out
The F0/80425 override byte has consumers for all8bits. Bit6 permitsFE/FF and holds one policy/event contributor, not the whole policy. D1..D5 reach the EC host-query queue, but their OS interpretation is unknown. Clearing bit6 can return savedSilent5 only through one power-state branch; it retains ordinary manual flags and does not immediately restore PWM. ActualPWM bank entries are read-only, so they are not an alternate mapped write route. Generic MCU register reads really access the EC, but outerstatus2 can conceal a separate bus error; failed reads leave the data area unchanged.

## Watch out
Registerzero is distinct from a zeroRPM request and from measuredzeroRPM. Normal equal-zero paths can later reapply remembered duty. Report raw counts, not physical percentages/frequency or guessed polarity. Generic writes replace bytes and have no masked/CAS semantics. Sharedbank0F is present in all8banks; the SMBus path does not inherit BRAM bank bounds or index wrapping. MCU helpers overwrite shared transfer state without first acquiring idle status; complete reentrancy and exclusive access remain unresolved. No hardware, installed settings, production capability or other-model admission changed. Trail-check lexical overlaps in older cumulative CHANGELOG/CI/wrapper changes are unrelated to this docs-only refinement.
