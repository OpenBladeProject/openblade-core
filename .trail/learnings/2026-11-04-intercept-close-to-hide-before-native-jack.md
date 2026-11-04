---
title: Intercept close-to-hide before native ClosingArgs allocation
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
Intercept user WM_CLOSE with the shared WindowsWindowCloseHandler before WinUI creates AppWindow.Closing arguments. Main and Dashboard keep their existing hide policies. Each attached view owns its registration; detach and WM_NCDESTROY clear the callback and native lookup entry. A false callback result forwards to Windows, while a callback exception is contained and consumes the close. Explicit Window.Close still destroys the shell. Keep the native smoke for repeated/system close, duplicate registration, replacement, failure and terminal cleanup.

## Tried and ruled out
In matched bare-window private-desktop controls, the same subscribed Closing handler saw 203 callbacks for 200 measured closes versus 3 callbacks for direct hides after identical warmup. Native ClosingArgs and its two adapters grew 3/53/103/203/203 only in the close arm: 128 payload bytes per close. Matching cached PDB symbols resolve the three types. With the production native interceptor, all three groups are absent at all five equivalent checkpoints. Forced collections in the diagnostic did not release the baseline groups; forced GC and private COM cleanup are not product fixes.

## Watch out
These censuses cover allocation-leading pointers on the default process heap in the selected modules, before terminal teardown; they do not prove all native memory is stable. The separate MRM subtree ownership leak and Intel-linked allocation investigation remain unresolved. Temporary heap-walk code must be archived outside the repository, removed, and clean outputs rebuilt before shipping. Keep programmatic close and application-exit behavior distinct from user hide-on-close.
