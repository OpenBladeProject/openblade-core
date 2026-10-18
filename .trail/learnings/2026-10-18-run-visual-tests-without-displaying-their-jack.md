---
title: Run visual tests without displaying their windows
author: Jack
date: 2026-10-18
branch: fix/hidden-visual-tests
touches:
  - ".editorconfig"
  - build/Capture-LocalizationSnapshots.ps1
  - build/CiImpactManifest.json
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs
  - build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj
  - docs/localization.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/TrayDashboardWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
---

## Decided
Run Tray visual smoke and localization-preview child processes on a unique Windows desktop created by test infrastructure. Never switch it into view, and fail if desktop creation or child launch fails. Create the desktop on a fresh helper thread so the calling runner stays on its original desktop. Preserve each child's Unicode environment and use explicit STARTUPINFO.lpDesktop.

Installer WPF tests cloak each real HWND with DWMWA_CLOAK before Show and verify DWM_CLOAKED_APP before and after Show. Keep noactivate styling and all existing rendering and logical-focus assertions. Use xunit.runner.json appDomain=denied for net472 so WPF text-services COM objects are not invalidated by secondary-domain teardown after the UI STA exits.

## Tried and ruled out
Per-test private desktop WPF STA threads passed focus assertions but explicit dispatcher shutdown stalled in text-services cleanup. A shared pumped STA on that desktop did not resolve teardown. DWM cloaking preserves software RenderTargetBitmap and the normal desktop's WPF lifetime.

## Watch out
CreateNoWindow and Start-Process -WindowStyle Hidden suppress initial console/show hints; apps can still show their own UI. Private Tray desktops lack Explorer, so harness builds must omit IsShownInSwitchers registration when GetShellWindow returns zero; production behavior stays unchanged. Keep the snapshot wrapper on the same private launch path and wait for timed-out children before desktop disposal. Never replace visual validation with a skip flag.
