---
title: Blade three-channel firmware profiles and I2EC read candidate
author: Codex
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - tools/firmware
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

The exact DI780, KI890, KI8100 and KI8110 carriers and payloads have offline inspector profiles. Both forms decode the same 2,048 mapped entries. The third stage and tach are mapped. The third actual PWM register is absent from the map.

## Tried and ruled out

All four carriers contain the same managed flasher with an indirect Super I/O read definition. Its read helper is used by a separate SPI ID method, with no managed call from the updater entry found. All four EC images write C3 to F0200D in initialization, which is consistent with I2EC permission.

## Watch out

The indirect path would bypass D0 if exact host decode, PWM address binding and nested-selector ownership are verified. It supplies no A1 publication or Auto completion proof. Keep profiles offline and do not infer real readings from a stage, feedback shadow or tach zero.
