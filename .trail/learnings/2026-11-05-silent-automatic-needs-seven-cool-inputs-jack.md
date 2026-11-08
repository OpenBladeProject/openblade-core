---
title: Silent Automatic needs seven cool inputs while legacy timeout owns storage
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-ec104-fan-analysis.md
  - docs/so6120-host-mailbox-alternatives.md
  - tools/firmware/profiles/so6120-ec-1.04.json
  - docs/so6120-silent-automatic-policy.md
---

## Decided

Investigate Silent Automatic and direct-control alternatives in parallel using the pinned SO6120 EC1.04 image. Ordinary comparator1526C uses inputs8053C..80543 and BF mask: any enabled input >=upper requests UP, every enabled input must be strictly <lower for DOWN. Factory stage1 lower bounds are50,50,46,50,46,35,33 for channels0,1,2,3,4,5,7. Limit40 means42 eligible evaluations from a cleared counter; a direction reset still increments in the same call. Targets decode after stage selection, and the tach/PWM cadence is separate. Keep counts distinct from seconds and factory data distinct from live policy.

## Tried and ruled out

CPU/GPU temperatures alone do not explain all seven inputs. Source aliases are bank0 indices10,11,30,32,34,17 for channels0,1,2,3,4,7; channel5 source80328 has no exact map entry. Channel7 strongly matches battery temperature: record29A1C has address16 command08 destination8035C; FE60 derives (word-2731)/10, clamped below zero and stored as a byte, then publishes80543. SBS1.1 section5.1.9 defines08 as pack temperature in0.1K. This is a source-backed inference, not physical readback or units for other channels. Overridebit5 can preserve its cached value. Flags80B08/80C08 can floor decoded targets to3000 and suppress other work; they are not fan-stop controls.

Legacy F0/F1/F3/F4/F5 are storage transactions. F4 already reaches persistent writes; F5 continues. Distinguish Silent auxiliary record2D (mode+28,256bytes to87C00) from curve25 (mode+20,512bytes to8A000); both are explicitly admitted by the persistent path. Countdown8044E skips conditional reload, not the ordinary controller. Expiry1819A clears mailbox bookkeeping without restoring records, targets or PWM, or requesting reload. Unchanged mode can still bypass reload after expiry.

## Watch out

Zero target is demand, not immediate or durable zero PWM:15510 has stepping/cadence/stability and cached-duty behavior, so tach history remains necessary. Reload scans differ from steady-state dwell; repeated mode writes can alter a comparison. Source sensor aliases are not an atomic snapshot of stage inputs and do not grant raw access. No generic register probe, flash operation, driver load or live comparison occurred. Next physical evidence, if later resumed, should use existing typed Automatic/tach operations and stable idle blocks; direct duty still lacks supported exclusive access, bounded cooling recovery and exact restoration. Do not generalize these addresses or semantics to another model.
