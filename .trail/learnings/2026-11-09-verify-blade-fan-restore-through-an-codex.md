---
title: Verify Blade fan restore through an independent register map
author: Codex
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
  - docs/fan-stop-model-coverage.md
---

## Decided
The four Blade 17/18 primary bank-zero A0 maps read the I2C bank byte at logical 0F regardless of the I2C-selected bank. A fresh 0 is an observation. The bank bound must still hold through submission. Cycle register F01841 is initialized to B7, so a fresh valid cycle below FF supplies a disjoint reply sentinel for any in-range duty, not just zero.

## Tried and ruled out
Bank 1 aliases six restore reads to one reset-cleared byte and routes the corresponding writes to scratch. Both primary bank guards can see zero around a falsely accepted same-I2C-map restore. Independent primary bank-zero verification catches it in the bounded model. A bank-zero single-byte 36 write can also be diverted if manual bit7 is set. Preserve a matching field.

## Watch out
A later bank-zero result cannot resolve an earlier ambiguous transfer. The 0529 reinit watcher re-enters the zero-bank setup. Handback still needs exclusive bank and descriptor ownership and a retained guardian transport. The signed DA550 BIOS 1.03 contains BIOS and ME; identical DSDT and bounded delta inspection did not identify a receiver image. Capsule GUIDs shared by boards do not bind EC identity.
