---
title: Preserve fan intent while projecting fresh curve targets
author: Jack
date: 2026-11-14
branch: fix/issue-967-model-admission
touches:
  - Directory.Build.props
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-blade-models.md
  - docs/source-reviewed-blades-2019-2020.md
  - docs/source-reviewed-blades-2021-2022.md
  - docs/source-reviewed-blades-issue-949.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
---

## Decided
Keep the issue 967 reviewed fan intent envelopes separate from cached observed firmware limits. IPC 82 carries an optional observed range map; protocol 51 strips it and caps all display placeholders at its original 5400 RPM bound. Saved profiles and editor points retain reviewed intent, including 5500 RPM, while typed curve samples project against fresh per-SET metadata and return confirmed actual targets.

## Tried and ruled out
Do not validate saved settings against a transient observed maximum, clamp editor intent to it, or cache requested targets as applied. Ordinary read-only state polls do not refresh range metadata: compare original curve intent with actual targets and let the typed update refresh limits without redundant SETs. Do not bridge Service-only interfaces into Protocol. Preserve the existing single-attempt maintenance branch for validated backends.

## Watch out
If a second target's fresh range excludes the first confirmed target, stop without another write or replay. Fresh metadata, paired selector state and adapter admission remain independent for every SET; reducing redundant GETs does not waive these checks. Trail's issue 967 conflicts are lexical matches or superseded bounded fan exclusions: minimum 2000 still rejects 0/1500, Base Gaming raw1 and Creator raw2 remain exact, modern raw2 MaximumPower does not apply to Base, firmware/Thunderbolt labels and old HyperBoost recovery ownership are unchanged. Failed adapter queries never become full power and no physical recovery is claimed.
