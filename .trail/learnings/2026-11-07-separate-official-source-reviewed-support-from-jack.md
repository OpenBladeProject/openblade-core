---
title: Separate official source-reviewed support from physical evidence
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - AGENTS.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/build-install-and-nightly.md
  - docs/developer-guide.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/keyboard-lighting-file-format.md
---

## Decided

The maintainer explicitly chose official source-reviewed laptop support with physical validation tracked separately. Model support is not the same as physical evidence: the catalog marks the original two validated models PhysicallyValidated and the nine reviewed-source profiles SourceReviewed. Keep exact-host/HID admission, per-feature source pins, bounded commands, independent readback, durable recovery and stop-on-ambiguity. Legacy Experimental backend/IPC identifiers remain for compatibility; they are not the product support classification.

Add RZ09-0508/02B6 (Synapse694), RZ09-0510/02B7 (695), RZ09-0509/02B8 (696) and RZ09-0529/02C7 (711) through each model's pinned sources. Their thermal queries use profile1 and transaction IDs1..31. Turbo(raw1), MaximumPower(raw2,711 only), AC Balanced0 and BatteryBalanced6 are distinct states. CPU Boost3 is admitted only on694/695;696/711 retain0..2. Charge limits are50..80 in5-point steps plus protectionOff100. Fixed/curve fan writes, lighting, HyperBoost, driver/THX integration, startup and temporary full charge are not admitted for this batch. Automatic handback is typed and preserves the confirmed preset without target writes. New performance enum values require service IPC73 and a coordinated product update; Session JSON IPC is unchanged.

## Tried and ruled out

Do not inherit older models' profile0 selectors,50..100/1% battery range, manual fan limits or lighting masks. The2024 source can query dynamic fan information;695 UI/MW defaults disagree, so static defaults alone do not admit manual targets. Empty battery SET ACKs are source-backed for all4 models, but only0..1bytes are accepted and any present byte must match; independent exact raw GET remains mandatory. Independent review caught two real new-path bugs: fan-only handback must preserve raw0 after AC-to-battery, and a battery preset must reject contradictory WindowsBattery/firmwareFullPower readings. Both have regressions.

## Watch out

Firmware carrier inspection establishes package identity/declared targets, not physical command behavior. Keep standard/Lamborghini and GPU/SSID-specific BIOS images separate. The driver-catalog ledger must cover every supported model and every artifact; only the new711 exact V4 native DLL match is Applicable, which grants no THX runtime or setup authority. Pin the catalog to the core commit containing the expanded model list, then refresh core fixtures in a follow-up commit to avoid a circular source pin. Retain source/owner observations and unresolved Blade18 reports without relabeling them successful physical validation. CPU/native overclocking UI concepts require tracing to the actual firmware-level setting; a generic feature name is not enough.
