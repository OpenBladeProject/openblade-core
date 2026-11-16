---
title: Separate external presence from adapter wattage
author: Jack
date: 2026-11-14
branch: fix/issue-967-ac-control-availability
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - docs/synapse-adapter-wattage.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.Core.Native/NativeAutomationPolicy.cs
  - src/OpenBlade.Protocol/AdapterPowerCommandClient.cs
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
---

## Decided
Treat fresh Windows external presence and adapter wattage classification as separate facts. All 22 models' own pinned Synapse ordinary setters remain reachable after an unavailable wattage getter. Keep ExternalUnknown truthful and use an explicit reviewed capability for ordinary thermal control; closed power-independent direct commands use their own existing contracts. IPC 83 adds false-default presence and admission metadata, while frozen read-only 51 omits both.

A completed correlated read-only 07/8C request may remain usable after raw terminal status 03 or 05. Validate the complete frame, checksum, transaction, command and error-body geometry; never decode rejected payloads as wattage. Preserve diagnostics. Transport failure, cancellation, busy/new/timeout, mismatched data, source contradictions and possible writes retain their stopping behavior. Ordinary purpose is explicit because HyperBoost shares a command ID; an active HyperBoost baseline cannot be exited through the ordinary exception.

## Tried and ruled out
Do not promote getter failure to FullPower, infer barrel versus USB-C from watts, pass PluggedIn to bypass native eligibility, swallow arbitrary IO exceptions, or merely enable UI buttons whose backend still refuses or ignores the action. Do not select an AC or limited-power profile from Unknown. Known-limited and battery policies remain a separate scope. Model-wide physical validation does not establish physical validation of this new source-reviewed admission.

## Watch out
Unknown manual intent needs request, power-generation and owner context: retain it through classification refinement on the same source, but never admit another user's dormant overrides. Source-free native override selection preserves the frozen v1 policy and ABI. Direct display setters require a separate verified positive-presence context and generation checks; profile application stays classified. A manual display hold must survive the first known classification and an active application profile. Named profile editors are distinct from direct actions; do not invent a destination slot. The original 967 archive omitted the raw numeric status, so software verification does not prove physical recovery of that laptop.
