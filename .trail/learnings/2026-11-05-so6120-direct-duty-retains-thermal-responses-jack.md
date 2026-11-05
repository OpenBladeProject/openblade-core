---
title: SO6120 direct duty retains thermal responses but lacks proven cooling handback
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-ec104-fan-analysis.md
  - docs/so6120-mcu107-ec-fan-bridge.md
  - docs/so6120-direct-duty-safety.md
---

## Decided
The exact SO6120 EC 1.04 / MCU 1.07 direct-duty path retains some thermal responses, but current evidence does not establish a bounded independent return to normal cooling. Keep production fan-stop admission withheld and the live Silent comparison paused. docs/so6120-direct-duty-safety.md records reviewed instruction locations and limits; this is static research, not hardware authority.

## Tried and ruled out
EC FE skips the normal fan loop after calling the supervisor. CPU/GPU PROCHOT and a high-sensor GPIO response survive, but the reviewed paths do not restore normal fans. Sensor read failure retains old data. The GPIO action resembles power-off signaling without proving board shutdown. Permission bit 6 also skips a non-fan policy calculation; retaining the Silent table does not prove Silent equivalence. MCU generic access does not pause its normal worker: failed transfers remain pending, cache serves both intent and independent readback, and the watchdog continues after host-process death. Examined USB cleanup is not a host-loss lease. No safe command sequence follows from these findings.

## Watch out
Report thresholds as raw units and counts, not Celsius or seconds. The warm zero-tach counter need not be consecutive. NDS32 beqs38 compares implicit a5 with the printed operand: FE compares requested duty against PWM, not the nearby RPM byte. MCU D0=50 is a separate telemetry countdown, not manual-readback delay; CC is the queue delay. Endpoint-0 switches and transport reentrancy remain incompletely recovered. Do not claim all protections disabled, all USB paths covered, or inevitable bus collisions. Reuse exact image identities and independent review; do not repeat live tests merely to confirm static facts. This docs-only change does not alter the old CHANGELOG, CI, or validation-wrapper code implicated by lexical trail-check overlaps in the cumulative branch diff.
