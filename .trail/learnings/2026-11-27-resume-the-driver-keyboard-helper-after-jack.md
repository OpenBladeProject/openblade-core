---
title: Resume the driver keyboard helper after a force-killed owner
author: Jack
date: 2026-11-27
branch: fix/driver-keyboard-orphaned-hooks
cost: { minutes: 30, tokens: 194558 }
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/DriverOwnershipJournalReader.cs
  - src/OpenBlade.SessionAgent/RazerKeyboardOwnedHookEvidence.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - tests/OpenBlade.SessionAgent.Tests/DriverOwnershipJournalReaderTests.cs
---

## Decided
The maintainer chose on 2026-11-27 to resume the Razer-driver keyboard helper automatically after a force-kill. This narrows the 2026-11-22 rule that automatic starts keep same-boot journals closed. Only an identified, unlatched journal qualifies: the journal records SupervisorProcessId and CreatedAt, and a supervisor that deliberately leaves it marks Latched. The mark is written before its own cleanup after an uncertain worker exit, and otherwise in the loop's finally. A same-boot start that finds that journal's supervisor and last worker dead (IsLiveSince checks PID and start time; the helper mutex excludes a live owner) clears only the hooks named in keyboard-driver-owned-<supervisorPid>.json, using TryReconcileHooks with confirmedOwned. Missing evidence means [], so any remaining OpenBlade hook refuses. The existing 5 s released-input wait then decides. Before each clear, TryReconcileHooks' recordRemaining rewrites the evidence to the hooks still to clear, without the in-flight one, so a mid-cleanup kill stays recoverable and an in-flight clear is never replayed. A failed recovery latches the previous journal. A latch mark that fails three times falls back to a ProcessId-0 refusal marker, then to deleting the evidence. Journals from 0.77.1 and earlier carry no owner, so they keep the explicit-retry rule and need one reboot.

## Tried and ruled out
Relying on evidence presence alone: an owner killed while idle (clean-pause wait) leaves a journal with no hooks and no evidence, and that case would never recover. Writing one refusal marker before the whole cleanup: a Fable delta review showed that a kill during the owner's routine cleanup (22-39 rearms a day on the 0581) then needed a reboot again. Deleting evidence only on uncertain cleanup: Environment.FailFast in RazerSnapTapNativeIo skips every catch and finally, so cleanup would be replayed on each start.

## Watch out
A .NET File.Move replace fails with UnauthorizedAccessException while another handle holds the target with FileShare.Read, which is the shared journal reader's mode. Full-solution builds need the Rust toolchain on PATH (CARGO_HOME/RUSTUP_HOME under C:\tmp\openblade-rust-1.99.1). Under a subst drive, CpuVoltageHostStateTests.LiveHostStillRequiresExactImageSessionAndLifetime fails because of the process image path, and the test passes from the real path. The Run-level sequencing has no direct test; only the helpers do.
