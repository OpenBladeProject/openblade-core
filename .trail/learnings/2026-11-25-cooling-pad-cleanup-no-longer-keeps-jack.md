---
title: Cooling pad cleanup no longer keeps the service run marker
author: Jack
date: 2026-11-25
branch: fix/cooling-pad-cleanup-run-marker
cost: { minutes: 10, tokens: 221747 }
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Service/ServiceControlRuntime.FanStop.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/Worker.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntime.CoolingPadLifecycleTests.cs
  - tests/OpenBlade.Service.Tests/ServiceStartupCoordinatorTests.cs
  - tests/OpenBlade.Service.Tests/WorkerTests.cs
---

## Decided
- The service run marker only selects laptop unexpected-exit recovery. A pad-only Blocked or Failed stop cleanup no longer keeps it (PR #1010). `PrepareForServiceStopAsync` returns `ServiceStopCleanupResult`; `LaptopStatus` (HyperBoost, fans, keyboard, display-brightness input) decides the marker, and `Result` still reports and logs the pad.
- The pad recovers through its own journal: `LoadOwnershipAsync` reads it on every start, and `SetUserAsync` sets RecoveryRequired and WritesPaused whether or not the marker remains.

## Tried and ruled out
- #1004's resume as the reason the pad was WritesPaused at stop. One handback failed before submission as `Disposed (0D/90)` (retired session), which kept the journal. After that every start paused writes, resume and the tray's automatic read were skipped (NeedsCheck), and every stop returned Blocked from `ReleaseAsync`. That waits for the user by design. #1005 reopens retired sessions in `ReadDeviceAsync`, which removes the Disposed trigger.

## Watch out
- `sed -i` in Git Bash rewrites CRLF files as LF. Count `\r` with `tr -cd '\r' | wc -c`, because `grep -c $'\r$'` reports CRLF lines even when the file is LF.
- `DriverModeReleaseOverride` returning false means "released", not failure. Use `DriverModeReleaseFailure` for a failed keyboard release in tests.
