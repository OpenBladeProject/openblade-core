---
title: Match display readback and retain unfinished extraction inputs
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

Capture the refresh baseline and classify external display changes inside the same serialized operation. A refresh queued behind a manual edit must compare with that completed edit. Tray color confirmation and profile comparison follow the Session Agent: a color profile is dormant while actual readback is HDR, and Windows color-profile identity ignores case. Refresh-rate keys remain ordinal. Preserve canonical option selection and labels when Windows reports a differently cased color ID.

A loaded durable THX recovery record stays recovery-required when its package directory is missing or untrusted. A typed recovery-open failure carries that distinction without allowing reacquisition or installation. Pre-intent cleanup clears only ReadOnly on a freshly admitted owned regular file. Retain the entire package before journal disposal if the owned cabinet extractor's exit cannot be confirmed, keeping both acquisition and stop failures. Extraction uncertainty is not hardware submission.

## Tried and ruled out

Read-only-file cleanup, missing/protection-rejected recovery directories and dormant/case-different color confirmation each failed deterministic regressions before correction. A refresh queued behind a user edit falsely reported an external change; a genuine later external change remains reported. The native GPU startup harness now checks both the main window and quick-controls window with known and delayed ranges, actual slider values and labels, and no service calls or pending writes.

A busy brightness edit is intentionally refused by both UI guards and restored to confirmed presentation. Empty supported brightness levels cannot be a successful configured native brightness result. Do not weaken these checks or combine invalid-state fallbacks merely to match synthetic values.

## Watch out

No old-directory sweep or submitted-package reclamation is authorized by this cleanup. Failed process termination must not become permission to delete a possibly active extractor's files. Keep bounded exit waits, protected-tree checks, independent installed-package readback, durable intent and exact-model admission. The original GPU-startup and Lighting-Off incidents remain unproven despite the new passing offline/native coverage; physical validation remains separate.
