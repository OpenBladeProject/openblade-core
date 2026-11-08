---
title: integrate the supervised guardian without changing Auto limits
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-fan-stop-crash-recovery.md
  - docs/so6120-supervised-pwm-trial.md
  - src/OpenBlade.Capture/Program.cs
  - src/OpenBlade.Capture/So6120SilentPwmCommand.cs
  - src/OpenBlade.Capture/So6120TrialGuardian.cs
  - tests/OpenBlade.Capture.Tests/So6120TrialGuardianTests.cs
  - tests/OpenBlade.Guardian.TestHost/Program.cs
  - src/OpenBlade.Capture/So6120GuardianHost.cs
  - src/OpenBlade.Capture/So6120GuardianProcess.cs
---

## Decided

The supervised SO6120 commands launch a separate Capture owner with its own console and an explicit inherited control-pipe handle. It must independently be outside every Windows job and be the sole attached console process before metadata or acquisition. Both attended approvals remain in the owner; the hardware-free caller can only heartbeat or request Auto. The existing session remains the only command publisher.

The attached control latch alone owns caller liveness from preparation through execution. The guardian operation cutoff starts after APPLY and disarms at the existing Restoring milestone, so Auto keeps its independent budget. Record a guardian receipt even when execution yields no Trial, and retain caller and guardian stop causes separately. Cancellation during either prompt is not a decline. Persist completion text and relay conservative outcomes in the caller because the owner's private console disappears on exit.

## Tried and ruled out

An actual launcher test under this managed host leaves an ancestor Windows job. Production admission correctly refuses it. The fake-only fixture independently proves sole-console attachment and escape from the dedicated caller job before continuing with an in-memory port. That demonstrates caller-loss survival and same-owner handback, not full production admission or live native-driver recovery.

Claude Opus 5.5/high completed one frozen working-tree review. Its ten items were addressed, including an operation timeout during Auto, missing caller outcomes, unhandled setup/reporting errors, lost null-Trial receipts, canceled prompts mislabeled as decline, and pipe backpressure after stop. The focused post-fix run passed 77 tests. A second Claude review was not run.

## Watch out

Keep native close, acquisition release, Auto readback, cleanup and journal completion separate. An unconfirmed native close holds the owner and resources indefinitely, including after console-output failure; there is no automatic retry, takeover, force-kill or new console reader. Send Auto once and continue draining caller bytes after stop so a retained owner does not fill the pipe. A pending native command still finishes confirmation or fences all commands, including A0.

This developer integration does not enable production Stop, admit another model or authorize hardware tests. Earlier physical zero-duty evidence remains USB-C Balanced and exact SO6120/RZ09-0581 only. Trail's branch-wide lexical overlaps do not justify reintroducing RPM-zero production commands, default-profile resets, generic FanMode.Stop, cross-model command copying or replay after ambiguity. Live guardian admission and caller-loss recovery still need a separately authorized attended test.
