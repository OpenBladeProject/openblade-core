---
title: Refuse malformed recovery records and preserve extraction failures
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningCommand.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningJournal.cs
  - src/OpenBlade.SessionAgent/ThxBaseAudioPackage.cs
  - src/OpenBlade.Tray.Core/DisplaySettingsViewModel.cs
  - src/OpenBlade.Tray.Core/ProvisioningPackageDirectory.cs
  - src/OpenBlade.Tray.Core/RazerDriverInstallJournal.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardDriverInstaller.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.GpuStartupHarness.cs
---

## Decided

Malformed keyboard journal syntax, shapes, kind types and unknown kinds require recovery before acquisition. A later complete record cannot erase malformed-record uncertainty. Validate the Windows Update recipe before retaining, then retain before the durable intent append.

THX restart execution uses the journal-admitted canonical package destination. Internal acquisition timeouts are failures unless the caller actually canceled; submitted uncertainty remains recovery-required. Aggregate or unsupported process-tree termination failures retain extraction input. Observe canceled output drains within a bounded wait and observe late faults without replacing acquisition or termination errors.

Brightness profile confirmation and current-profile comparison share a strict predicate: configured brightness requires non-null current readback and a nearest supported level. Missing or empty metadata cannot establish success or equality.

## Tried and ruled out

Five THX regressions failed with two caller-cancellation controls passing before correction. Four malformed brightness snapshot cases exposed null collection failure, inconsistent profile difference and null-equality false confirmation. Valid native brightness success still requires supported levels; do not fabricate a fallback confirmation.

Windows Update pinned-package fallback after an intent is unreachable: only the initial no-offer result before the callback can fall back. No submitted/completed-package sweep or cached reparse/ACL admission was introduced.

## Watch out

The extraction process is data-only and separately owned. Failed termination does not imply hardware submission, but its directory must survive disposal. Preserve both acquisition and stop causes. These are isolated filesystem/fake-process tests, not installation or firmware validation.
