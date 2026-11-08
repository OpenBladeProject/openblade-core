---
title: Verify installed CPU rolling-average Auto restoration
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-stop-auto-guardian.md
---

## Decided
The broker-installed a9e97f70 build passed the RZ09-0581 USB-C Balanced thermal-exit trial. Guardian Stopped was observed, followed by autonomous Auto on a CPU rolling average of 51.19197356 C against a temporary 45 C limit, with a five-second window and full five-second coverage. No client Auto request was made. Independent released-Auto readback and exact restoration of all ten original settings passed; CPU returned to the original 55 C average and 65 C cutoff settings.

## Tried and ruled out
This result supersedes the earlier SafetyCondition trial as thermal-exit evidence; do not relabel the old trial. The independent sampler and cooperative power checks keep fresh temperature monitoring active through serialized native work. All 372 installed files matched the reviewed package. The final typed harness and dependencies were hash-frozen at the same revision, and independent review verified the saved result.

## Watch out
The 361 snapshots contained no blocked or unconfirmed writes, RecoveryRequired state or controller warning. Host-observed return and settings restoration occurred about 28.6 and 54.6 seconds after dispatch; those are not exact physical fan-stop durations. Keep the result scoped to this model and exercised path. Sustained Stop, automatic entry, sleep/resume and actual power-source transitions remain separate physical-validation work.
