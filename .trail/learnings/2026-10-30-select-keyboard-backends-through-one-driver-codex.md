---
title: Select keyboard backends through one driver owner
author: Codex
date: 2026-10-30
branch: feat/keyboard-input-backends
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/FunctionRowKeyboardHookProcessor.cs
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverProtocol.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverTransport.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapHookSession.cs
  - src/OpenBlade.SessionAgent/ServiceReactiveLightingLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
---

## Decided
Users explicitly requested independent Windows-hook/helper and Razer-driver choices for both SnapTap and the multimedia/function row. This replaces the earlier driver-only SnapTap and A/D-only driver feature policy. Preserve existing defaults: Razer driver for SnapTap, Windows hook for the function row. Unknown values pause their feature; transient preference reads preserve confirmed choices. Choices are per-user and do not require an IPC protocol change.

One supervised driver worker must own the complete selected typed hook set. The pinned subscriber review in docs/snap-tap-0255-catalog-api-review.md establishes a global per-device hook table and per-file notification fanout (.76 RVA 60A4). Independent A/D and function-row subscribers can consume one another's keys during setup and closure. Stop the full driver subscription, prove child exit and canonical restoration, then start the newly selected union. Windows SnapTap and a driver-owned function row may coexist because their input sets are disjoint.

## Tried and ruled out
Do not infer driver-event safety from table-preservation tests alone. Separate subscriptions, ignoring other-feature packets, or treating any matching canonical record as ownership do not solve event loss or crash recovery. Production admission rejects nonempty preexisting hook tables before subscription; preserve foreign records rather than clearing them. A Windows backend choice must not bypass an outstanding A/D driver ownership journal.

## Watch out
Windows SnapTap uses fixed A/D scan positions globally across keyboards; game compatibility differs from device-level interception. Function-row driver support remains exact RZ09-0581/RZ09-0528 and source-backed experimental, even when that model's A/D driver path was physically validated. Keep backend and scope provenance separate in status and diagnostics. Only the driver helper observes Fn metadata, with no second low-level hook, and actions use existing typed service routes. No new firmware commands, models, raw writers, installs, or development-time input writes are authorized by the selector work. Offline and disconnected UI checks do not establish game or physical driver success.
