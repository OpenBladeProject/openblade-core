---
title: Recover written fields and preserve frozen legacy client schemas
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
---

## Decided
Record explicit ThermalOnly versus All recovery obligations. A thermal-only failure on a limiter-only source backend must not demand an unrelated charge GET absent from older firmware. Keep battery state unknown, skip that GET, and preserve default/unknown/battery and override-capable requirements. Broader obligations never narrow; factory scope acceptance and pause release stay atomic under the runtime/provider gates.

## Tried and ruled out
Swallowing an error after issuing the unsupported recovery GET would leave a poisoned fresh session. Prevent the unrelated query instead. Do not change policy-only write confirmation just because a backend can query temporary full charge: actual written fields determine that scope, known active overrides still mismatch, and pending All obligations remain strict. Exact paired DMI identity plus reviewed PID is the model boundary; regional SKU is diagnostic.

## Watch out
Legacy read-only projection must be tested with frozen old DTOs, enums and strict unknown-member handling, not the current reader. Protocol51 has six presets, six Windows power roles and the original setting-source numbers. Omit newer members, leave unsupported observations unknown, and mark mandatory display placeholders as profile intent. Keep current IPC serialization unchanged. Advisory review findings require reproduction; refactoring suggestions and future hypothetical drift are not confirmed defects.
