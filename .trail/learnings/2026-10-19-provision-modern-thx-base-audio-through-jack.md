---
title: Provision modern THX base audio through exact bound evidence
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - src/OpenBlade.Session/SessionAudioIpcCodec.cs
  - src/OpenBlade.Session/SessionAudioMessages.cs
  - src/OpenBlade.SessionAgent/OpenBlade.SessionAgent.csproj
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/SessionAudioRequestDispatcher.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningCommand.cs
---

## Decided
Modern THX setup in draft PR #819 includes exact-model base audio before THX: Realtek6.0.9906.1 for RZ09-0528/RZ09-0530, IntelSST20.43.12634.0 plus trusted Windows inbox SDCA for RZ09-0581. Runtime pins are cross-checked against catalog metadata. Only the named root INF and its declared CopyINF closure may be installed; verify every root match, including CompatibleIds, to exclude incidental targets. Preserve unknown/newer bindings. Legacy0482/0483/0484 remain blocked on the original endpoint InterfaceSetting/FxProperties binding, not missing acoustic data.

## Tried and ruled out
Do not stage all Intel INFs separately or install CAB wildcards. CopyINF imports the root closure. Do not infer a Windows version floor from the observed servicing build, or require the observed HID package as an audio prerequisite. A native read-only Capture passed on the exact0581 with actual bound root/OED/SDW/closure children and SDCA ancestry; it does not validate fresh installation or restart. Keep optional DetectionVerificationDrv at its own source-normalized1.0.4139.0 version, not the Intel20.43 version.

## Watch out
Windows may encode an empty optional CompatibleIds list as a single UTF-16 NUL; accept that only in the provisioning parser and keep HardwareIds strict. Stable journal IDs0/1/2 retain their old meaning; format2 orders base step3 before them. Invalid checkpoints invalidate continuation. InvalidDataException needs explicit containment before and after durable intent. Recheck active session, host, target, power and controllers immediately before process submission. Structured discovery-only diagnostics carry sanitized bounded proof without device targets. For dotnet format use paths relative to the project and verify nonzero formatted file counts; absolute include paths previously did nothing. No development-time installation or audio mutation was performed.
