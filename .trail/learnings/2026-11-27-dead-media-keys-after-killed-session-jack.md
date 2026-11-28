---
title: Dead media keys after killed Session Agent leave orphaned driver hooks
author: Jack
date: 2026-11-27
branch: claude/media-keys-not-working-cb9d18
cost: { minutes: 1, tokens: 2959 }
---

## Decided
Media keys dead on RZ09-0581 (0.77.1, 2026-11-27) were not a code regression. The Tray and Session Agent were killed abruptly at 2026-11-27T02:32Z (no shutdown log lines; Explorer relaunched the Tray). The SnapTap/function-row driver supervisor never ran hook reconciliation, so the Razer driver still holds OpenBlade-purpose hooks. The new supervisor stops at OwnershipJournal with RecoveryRequired, Context `RetryRequested=False; SameBoot=True; OwnedHooks=True; InputReleased=False` (SnapTapDriverSupervisor.cs:86-109). Only a reboot clears it today. A possible fix was offered and not built: on explicit retry, remove exactly the hooks listed in the dead supervisor's `keyboard-driver-owned-<pid>.json`, after proving the supervisor and worker PIDs are dead, the boot is the same, and enumeration matches the evidence. This relaxes the deliberate 2026-11-22 rule, so it needs maintainer approval.

## Tried and ruled out
Explicit retry (switching input methods) cannot recover this case: release requires `!ownedHooks`. Windows-hook function row is also blocked by the same journal. The running `openblade_windows_service_candidate` cargo test binary was unrelated.

## Watch out
Fast triage: read %LOCALAPPDATA%\OpenBlade\function-row-status-1.json and snap-tap-status-1.json, plus OpenBlade.SnapTap.log; an OwnedHooks=True RecoveryRequired means a killed supervisor, not a key-mapping bug. Force-ending OpenBlade in Task Manager reproduces it. `trail grep` across the multi-repo cache timed out at 120 s here; plain Grep on src was faster.
