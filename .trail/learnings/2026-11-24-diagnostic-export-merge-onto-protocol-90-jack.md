---
title: Diagnostic export merge onto protocol 90
author: Jack
date: 2026-11-24
branch: feat/native-export-diagnostics
touches:
  - tools/openblade-windows-host/src/diagnostic_export/sections.rs
  - tests/OpenBlade.Service.Tests/NativeDiagnosticExportVectorTests.cs
  - tools/openblade-windows-host/tests/data/diagnostic-export-native.json
  - tools/openblade-windows-host/tests/data/diagnostic-export-contract.json
---

## Decided
- Main's new DiagnosticArchiveProvider keys (b82a614d, 5dec9093, 0426e6a4, a4bc0874) read only status members, so the native export ports them from its published status. No export case needs a pending-parity entry. A typed capability record native never composes (intelCpuPowerLimit, localDimming) goes into sections.rs UNEXPORTED and prints False. An access member or enum member is read like hyperBoostAccess.
- A key that prints a new managed enum needs that enum type in `NativeDiagnosticExportVectorTests.Enums`, then a contract rerecord. sections.rs looks names up in the contract file and panics on a missing enum.

## Tried and ruled out
- Rerecording `diagnostic-export-vectors.json` does not show gaps from new provider keys. Only the five refusal replies changed, and only their version. The vectors cover redaction, history and typed bundles, and none of those read the status.

## Watch out
- Only `NativeDiagnosticArchive_ReadsThroughTheManagedCodecAndMatchesTheManagedProvider` catches a new managed key. Rerecord `diagnostic-export-native.json` in the export lane (`OPENBLADE_REGENERATE_NATIVE_DIAGNOSTIC_ARCHIVES`) and rebuild Service.Tests, because the json is copied PreserveNewest at build. Then run the managed test.
- Under PowerShell, `[IO.File]` relative paths resolve against the process directory, not the `cd` location. A relative path can write into another worktree.
