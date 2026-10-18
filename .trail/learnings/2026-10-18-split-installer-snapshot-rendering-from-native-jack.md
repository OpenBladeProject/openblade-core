---
title: Split installer snapshot rendering from native focus isolation
author: Jack
date: 2026-10-18
branch: fix/hidden-visual-tests
touches:
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs
  - docs/localization.md
---

## Decided
Use two installer test paths. Noninteractive screenshots and the input-rejection test stay on the original desktop with DWM cloaking verified before and after Show. The real keyboard-focus test and actual-HWND desktop regression use a private desktop with the original Show/Activate behavior and all focus assertions intact. Keep that private desktop for the finite testhost lifetime, with xUnit appDomain=denied.

Resolve the private desktop on the runner before starting the fresh STA. Creating it from the STA can initialize apartment helper windows during thread joins and cause SetThreadDesktop to fail. The UI thread attaches before any WPF operation.

## Tried and ruled out
Closing a shown progress view on the alternate surface stalled teardown after PNG generation. Treat rendering and native activation as separate backend requirements; preserve each check on the path proven to complete.

## Watch out
This supersedes the all-private installer design in the prior learning. All 164 installer tests passed in Debug and Release, and all seven qps-ploc screenshot states completed on the split path. There is no visible fallback and no skipped visual test. Only the two explicit isolatedDesktop:true call sites may activate real test windows without compositor cloaking.
