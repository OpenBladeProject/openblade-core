---
title: Reject unsupported ArgumentList before private-desktop launches
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/TrayDashboardWindow.xaml.cs
  - tests/OpenBlade.Testing/WindowsVisualTestDesktop.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
  - tests/OpenBlade.Tray.Tests/WindowsVisualTestDesktopTests.cs
  - build/benchmarks/NativeResources/results/2026-11-04-window-closing-subscription-control.json
  - src/OpenBlade.Tray/App.WindowCloseSmoke.cs
  - src/OpenBlade.Tray/WindowsWindowCloseHandler.cs
---

## Decided
WindowsVisualTestDesktop.Start accepts the existing ProcessStartInfo.Arguments command line and now rejects a nonempty ArgumentList before environment allocation or CreateProcessW. Use reflection plus IEnumerable so the shared source still compiles under Windows PowerShell and .NET Framework, where ArgumentList does not exist. The regression uses a deliberately nonexistent executable and requires ArgumentException with parameter startInfo, proving rejection happens before launch. Preserve the existing quoted-Arguments/environment/desktop tests.

## Tried and ruled out
A temporary census controller supplied ArgumentList, which the custom CreateProcessW launcher previously ignored. The developer Tray therefore ran normal startup and signaled the already-running installed Tray to show, then exited through the existing-instance check before service or Session Agent startup. No intended reports existed; exclude that entire attempt from native-allocation evidence. The corrected controller used Arguments, a fresh output directory, exact callback counts and complete heap-census acceptance checks.

## Watch out
An unswitched private desktop isolates rendering and input focus, not named events or other process IPC. Missing diagnostic arguments can still activate an installed instance. Reject unsupported launch configuration rather than silently dropping it. Keep launchers C#5-compatible and verify with Windows PowerShell Add-Type as well as modern .NET tests. Do not alter the installed window to guess its pre-probe visibility.
