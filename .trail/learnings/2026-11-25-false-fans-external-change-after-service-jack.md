---
title: False Fans external change after service restart
author: Jack
date: 2026-11-25
branch: fix/crash-recovery-automatic-fans-drift
cost: { minutes: 14, tokens: 266494 }
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.UnexpectedRestartAutomaticFans.cs
---

## Decided
- After an unexpected-exit restart, user activation latched Fans as changed outside whenever the deferred manual-fan safety state did not converge. Recovery seeds PendingUnowned(ManualCurve), so firmware Automatic always turns it Lost. The latch now skips the case where the confirmed fans and the resolved fan intent are both Automatic. Manual intents that did not converge still latch (FreshAutomaticStateBlocksStaleThermalFloor, InitialUserActivationClassifiesRecoveryDrift).
- The trigger on the 0581 was the service run marker kept on every intentional stop. Cooling-pad shutdown cleanup returned Blocked ("Read the cooling pad before restoring retained settings"), so each reinstall started in unexpected-exit recovery.
## Tried and ruled out
- A real outside controller: GetStatus had no PotentialControllers, and confirmed = desired = Automatic with convergence Matched.
- PR #1004 and the keyboard PRs as a direct regression. They don't touch drift classification; #1004 only feeds the pad cleanup that keeps the marker.
## Watch out
- ProgramData service logs need admin. The Application event log (provider OpenBlade.Service) has the stop-cleanup and run-marker messages. ExportDiagnosticsAsync and GetStatusAsync through the installed Tray.Core ServiceClient are read-only, and the diagnostics hold only the current run's log.
- Whether a pad-only Blocked cleanup should keep the laptop run marker is still open.
