---
title: Synchronize broker child release and tray icon assertions
author: Jack
date: 2026-10-29
branch: feat/monolith-branding
touches:
  - AGENTS.md
  - tests/OpenBlade.DeploymentBroker.Tests/WindowsBrokerInstallerProcessRunnerTests.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
---

## Decided
Async tests must wait for the state they assert. TrayIcon_UsesQueuedPerformanceThenReturnsToConfirmedMode waits for both SelectedPerformance and TrayIconState at each transition because the presentation callback assigns them sequentially. Broker binding-failure tests use the existing manual-reset named-event child helper for release.

## Tried and ruled out
Do not access a runner-owned Process or its StandardInput after signaling child exit: the runner can already have disposed it. Do not use arbitrary sleeps or remove icon, exit-code, or binding-failure assertions to hide races.

## Watch out
The Monolith validation exposed both races in unchanged tests. They are test synchronization fixes only, not a production status-pump or installer-runner change. A full suite can fail at either preview or confirmed-icon assertions if it waits only for SelectedPerformance. Keep the event alive through awaited runner completion so an early release remains observable by the child.
