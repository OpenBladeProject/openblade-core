---
title: App profiles require durable display ownership and bounded telemetry
author: Jack
date: 2026-10-21
branch: feat/app-profiles-telemetry-battery
touches:
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
  - src/OpenBlade.Tray.Core/MainViewModel.Telemetry.cs
  - src/OpenBlade.SessionAgent/ApplicationProfileReporter.cs
  - src/OpenBlade.Tray.Core/TelemetryRecordingSession.cs
---

## Decided
Application rules match exact executable paths. The service resolves manual controls above the active application above the current power-source base. Keep the foreground winner separate from the latest activated still-running process, retain real process handles, bind exit callbacks to activation sequence, and renew admitted interactive-session context before the service lease expires. Editing, disabling, or removing an active rule must reconcile immediately.

Display automation uses an acknowledged atomic write-ahead recovery checkpoint. Persist the affected uncertainty mask before submission; preserve its union through manual admission, cancellation, and late automatic results. A partial explicit query cannot clear uncertainty for an unread field. Manual holds survive application changes and restart, then clear at the confirmed power-source boundary. Application exit restores owned baseline fields before ordinary base defaults. Missing checkpoint files are fresh installs; valid legacy source-only checkpoints migrate; existing corrupt files pause writes.

Telemetry reuses the existing observation loop. Sample every accepted observation even when service Revision is unchanged, because Revision is a change counter rather than a sampling clock. Unverified devices produce missing values. Keep replay events separate, statistics over raw samples, and chart gaps for missing/stale readings. Bound recording queues and reserve CSV End-row bytes; normal capacity/duration limits finalize a readable destination, while writer faults retain an accurately reported partial. Supervise writer completion and drain accepted appends before manual Stop; disposal must continue after a writer failure.

## Tried and ruled out
Do not treat successful setters as independent confirmation, replay a canceled display operation, restore guessed refresh-rate state, narrow uncertainty to the latest manual field, or let optional THX failure handling silently park mandatory app-profile observation. Do not deduplicate telemetry by service Revision, use downsampled points for statistics, or regenerate replay events from only visible samples. Discovery reads bounded launcher metadata and suggests executable paths; it never executes them.

## Watch out
Profile schema 22, export schema 14, service IPC 71 and display IPC 2 require a coordinated full install. Per-user brightness uses exact internal-monitor WMI binding and reported levels. Battery history is an explicit current-user Windows report, separate from live telemetry. Preview the battery preset before saving. App, telemetry and battery screens have disconnected localization routes; serialize builds and preview capture because the Tray test build also rebuilds localization-preview output. Publish production payloads from a short exact-commit worktree outside OneDrive, and use only the enrolled fixed-operation broker for an authorized unattended install.
