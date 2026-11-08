---
title: Preserve cooling-pad ownership across proven local admission deferral
author: Jack
date: 2026-11-07
branch: feat/cooling-pad-source-support
touches:
  - Directory.Build.props
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
---

## Decided
Cooling-pad control separates durable Windows-session ownership from finite per-command admission epochs. A periodic classification refresh may defer a tick without losing ownership only when the native actor proves local revocation before any SET, with no partial mutation or uncertainty. Real controller or identity boundaries still revoke; ambiguous submission still pauses. User transitions restore the protected original service baseline before rebinding the next user's inert settings.

## Tried and ruled out
Do not interpret trail check's lexical matches for configured cooling targets as tachometer claims: both Fixed configuration and Smart demand are labeled configured targets, and measured speed remains unavailable. New typed read/settings IPC is unrelated to swallowing failed status reads. Mirror publication clears only its owned software snapshot on failure and rethrows; microphone uncertainty and function-row admission remain intact. Screenshot fixtures and localized pad labels do not reintroduce GPU harness writes, drag overlays or detached setting rows.

## Watch out
The November 7 source-backed pad implementation intentionally adds a dedicated connected-only menu page, bounded current-user opt-in demand leases and latest completed keyboard-frame observation. Trail check can report word-overlap conflicts with earlier GPU, profile, drag-reorder and microphone learnings; review the actual diff and explain those false positives in the PR rather than changing unrelated behavior. Physical proof remains separate from source review and simulated validation.
