---
title: Deferred driver keyboard subscription latched RecoveryRequired
author: Jack
date: 2026-11-22
branch: fix/driver-keyboard-deferred-subscription
cost: { minutes: 8, tokens: 192879 }
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.SessionAgent/RazerSnapTapHookSession.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapNativeIo.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - src/OpenBlade.Tray.Core/SnapTapRuntimeStatus.cs
  - tests/OpenBlade.SessionAgent.Tests/KeyboardDriverStatusStoreTests.cs
  - tests/OpenBlade.SessionAgent.Tests/RazerSnapTapHookSessionTests.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
---

## Decided
After PR #990, RZ09-0581 still latched RecoveryRequired at 2026-11-22 02:37: a rearmed worker set its hooks, then the subscription enable was refused by canEnable (AllReleased/functionContext.Ready/shouldStop) before DeviceIoControl. HookSession reported MayHaveSubmitted=True because mutationPending was set; the worker exited 1, the guardian's reconciliation succeeded, but any non-0/3/8 exit keeps the ownership journal, so every later start (and the Windows-hook function row via DriverFunctionRowOwnershipRecoveryRequired) failed until reboot. Fix: canSubmit refusals throw KeyboardSubmissionDeferredException; HookSession reports SubscriptionDeferred with MayHaveSubmitted=false; the worker publishes Paused + Run/HookSetup/SubscriptionDeferred and exits 8; IsCleanPause (renamed from IsCleanForegroundPause) admits that exact stage+reason pair alongside InputLoop/ForegroundUnavailable.

## Tried and ruled out
Waiting inside SetHookEnabled for release with hooks installed but unsubscribed: unknown whether RzCommon swallows matched keys without a subscriber, so clear-and-rearm is the known-state path. Treating every pre-submission admission refusal ("Keyboard driver admission changed") as clean: that is a real device change, not a transient input state.

## Watch out
A journal written before the fix stays until the next boot; the installed build cannot clear it. Each rearm cycle repeats the full hook set/clear with readback. sed -i in Git Bash strips CRs from CRLF files; re-normalize with sed -b.
