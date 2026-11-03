---
title: Keep queued edits fresh and retain provisioning recovery inputs
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Platform/WindowsGpuAppManager.cs
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.SessionAgent/SessionGpuRequestDispatcher.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningCommand.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningJournal.cs
  - src/OpenBlade.Tray.Core/DisplaySettingsViewModel.cs
  - src/OpenBlade.Tray.Core/RazerDriverInstallJournal.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardDriverInstaller.cs
---

## Decided

Serialize display no-op checks, brightness target snapping and edit baselines together after any queued refresh. Keep the user's original power-source intent and emit a profile-edit event only after a real confirmed mutation. GPU activity scans retain their prime/reset/stable-intersection rules while awaiting cancellable delays instead of sleeping a worker thread.

A same live Windows-session request waiting behind its own finite handler owner must not invalidate that owner's permitted deferred fan-recovery retry. Match SID, session ID, logon kind and live authority. Preserve every competing admission lease and advanced epoch; different-session and canceled A-to-B-to-A safety remains unchanged. The ordinary profile activation scenario did not reproduce the defect; the known-not-applied deferred fan-floor retry did.

The keyboard and THX provisioning journals own only the new GUID acquisition directory for the current attempt. Validate protected containment, a complete bounded tree and no reparse points before cleanup; keep the journal locked. Mark package retention before attempting a durable intent append, including a failed flush. Recovery, submitted and completed packages remain retained. Do not sweep past attempts or infer ownership from a supplied destination path.

## Tried and ruled out

Native disconnected WinUI tests preserved enabled125/500 MHz controls and labels when GPU ranges arrived after saved enabled intent. Lighting tests preserved the restore action for confirmed Off versus active Spectrum across AC, battery and USB-C, including unknown-to-known power. These fill regression coverage but do not identify the original issue499 or574 cause. Trap service calls in the native fixture before applying synthetic status.

Claude's claimed permanent Maintain closure was disproved by guarded fresh-observation admission. Uncertain-write replay remained blocked by central pending-write ownership. Display completion runs before coordinator disposal because production joins the server dispatch first. Successful configured brightness with empty levels is not a production shape; do not weaken confirmation to accommodate it. Localized failure summaries intentionally keep raw backend diagnostics out of presentation.

## Watch out

A two-minute read-only observation of an already installed different commit is neither an idle benchmark nor evidence about the audit branch, battery drain, startup or long-term leaks. Synthetic and disconnected tests grant no hardware authority. Preserve original failures through fixture cleanup and keep caller cancellation observable. Keep tests synchronized with actual phase completion rather than matching an early transient property notification.
