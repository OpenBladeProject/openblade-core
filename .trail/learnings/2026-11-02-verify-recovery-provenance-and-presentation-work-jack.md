---
title: Verify recovery provenance and presentation work through real callers
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray/App.PerformanceHarness.cs
  - src/OpenBlade.Tray/MainWindow.Telemetry.cs
  - tests/OpenBlade.DeploymentBroker.Tests/WindowsBrokerInstallerProcessRunnerTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.LaptopReviewFollowup.cs
---

## Decided

Telemetry completion does no presentation work while telemetry is disabled or the presentation is inactive. A disconnected native regression should invoke actual telemetry capture plus completion, not only synthetic reference changes, and prove one chart/freshness update while preserving no-sample time progression.

Recovery status keeps actual observed power and authoritative current targets, but explicitly explains when the destination profile awaits application and controls still show previous targets. Do not mutate Automation just to make presentation appear applied.

Process-fixture cleanup always observes the monitor even if joining the child fails. Preserve the original assertion/readiness error together with cleanup failures; avoid an unread redirected stderr pipe.

## Tried and ruled out

An off-dispatcher reference read did not establish the alleged lost telemetry render: the loop awaits dispatcher capture and notifications, and a newer sample/source owns its own presentation. Uncertain HyperBoost entry keeps independent saved intent/journal provenance; fan-authority invalidation does not clear it. Normal reconciliation consumes that saved intent independently of the manual-curve latch. Do not preserve stale exact fan authority across an actually external HyperBoost interval.

## Watch out

No getter or restart can confirm an ambiguous write-only microphone command, and malformed recovery records stay fail-closed. The explicit read-only recovery boundary is not permission to proceed with deferred hardware work. Report reviewer limitations and rejected claims alongside fixes; advisory findings are not evidence until their complete production paths are traced.
