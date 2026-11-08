---
title: Keep guardian fixtures independent of host setup latency
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - tests/OpenBlade.Capture.Tests/So6120DirectDutySessionTests.cs
  - tests/OpenBlade.Capture.Tests/So6120GuardianIntegrationTests.cs
  - tests/OpenBlade.Capture.Tests/So6120GuardianLaunchTests.cs
  - tests/OpenBlade.Capture.Tests/So6120GuardianProcessTests.cs
  - tests/OpenBlade.Capture.Tests/So6120TrialGuardianTests.cs
  - tests/OpenBlade.Guardian.TestHost/FakeTrial.cs
  - tests/OpenBlade.Guardian.TestHost/GuardianLaunchFixture.cs
  - tests/OpenBlade.Guardian.TestHost/Program.cs
---

## Decided
Guardian fake-process tests use the fixture clock for semantic guardian/session/signal deadlines while retaining real IPC and process watchdogs. The live-lease unit test uses an independently advanced fake lease clock after the transfer reaches its gate, so preparation speed cannot consume the lease and lease expiry does not accidentally expire the separate native transfer. Two-minute outer waits are test deadlock guards only. Production timing, safety admission, journal format and disk durability are unchanged.

Await synthetic guardian completion before deleting its fixture journal. Retain evidence if drain is unconfirmed. Child cleanup may terminate only the caller returned directly by Process.Start; a process reopened from a reported PID is observation-only because the PID could have been reused. Preserve primary test failures while reporting cleanup failures.

## Tried and ruled out
Run 42459395640 failed 25 guardian cases; routing test TEMP/TMP to RUNNER_TEMP improved measured 32-record flush time from 153ms to 18ms in run 42462980901, but 18 cases still failed. Faster storage alone was insufficient. Real 2-second lease and 30-second operation clocks could run during durable fake preparation. Advancing a shared lease/session clock by two seconds also expired the native transfer, so the lease-specific test needs an independently controlled clock.

The earlier no-build local comparison accidentally reused Debug outputs from 374a2c86. It is not exact-head proof; rebuild before comparing. A fresh bc5a8039 Debug build passed all 56 affected pre-fix cases with two logical CPUs. Hosted validation remains required for the corrected fixture behavior.

## Watch out
Do not weaken journal durability, skip failure coverage, or expand production deadlines to fix slow test infrastructure. Keep actual timer-callback coverage for lease expiry, actual EOF/job boundaries for process tests, and restoration assertions. A test timeout must not be replaced by an unrelated locked-file cleanup error. The installed application source and completed physical Stop/Auto trial are unchanged by these test-only fixes.
