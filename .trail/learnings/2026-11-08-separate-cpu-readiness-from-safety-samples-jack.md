---
title: Separate CPU readiness from safety samples and reconcile completed setup
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - build/PawnIoPrerequisite.ps1
  - build/Publish.ps1
  - build/StoreSubmissionValidation.ps1
  - docs/so6120-cpu-temperature-source.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.CpuTemperature/Program.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.Platform/CpuTemperatureProtocol.cs
  - src/OpenBlade.Platform/WindowsCpuTemperatureChild.cs
---

## Decided
The CPU helper has a separate10s Ready handshake that cannot carry a temperature; callers remain bounded to1s, and every accepted measurement requires a new sequenced request. The new guardian must explicitly prepare this reader before its initial one-shot safety read and before opening fan control; otherwise each activation could die on its own cold-start null. Retain an actual process-exit observation after the short cleanup wait, so late confirmed exit releases the replacement fence. Use SemaphoreSlim's own timed wait; never abandon an independently wrapped acquire. Terminal worker failures are distinct protocol outcomes, and persistent failures use bounded backoff.

Setup reconciles known failed/completed PawnIO attempts read-only and returns its verified binding once. Unknown submission is never replayed. Bind recorded process identity to creation time/path, and use monotonic uptime rollback only as positive reboot evidence for otherwise unbound intent; wall-clock strings cannot establish reboot. Unknown absence or insufficient reboot evidence remains paused. The source-built LHM DLL participates in OpenBlade signing/readiness by its exact payload path; the original signed PawnIO installer is unchanged.

## Tried and ruled out
Claude's frozen initial-snapshot review found eight actionable lifecycle/installer defects; fix and regression-test them. The ninth suggestion to move the diagnostic observer was maintenance-only and would widen the change into unrelated PDH/NVML observers. Console standard input ignores cancellation after synchronous read begins: bound its wait independently and exit without starting another reader. Do not kill every cold start at the1s sample limit, reuse warmup values, declare250ms cleanup timeout a permanent telemetry failure, or clear an uncertain installer marker merely because the package exists.

## Watch out
The original full regression suite passed12549 tests; rerun affected suites and final packaging after fixes. A fresh guardian is unlike a long-lived polling service: background initialization cannot rely on a later poll before its first safety admission. No sensor/installer invocation or fan write was part of these offline checks. Physical sensor/lifecycle validation remains separate from source-backed implementation and packaging.
