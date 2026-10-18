---
title: Keep installer focus tests on a process-owned private desktop
author: Jack
date: 2026-10-18
branch: fix/hidden-visual-tests
touches:
  - build/CiImpactManifest.json
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs
  - build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj
  - docs/localization.md
  - tests/OpenBlade.Tray.Tests/WindowsVisualTestDesktopTests.cs
---

## Decided
Installer UI tests now share one private Windows desktop for the testhost lifetime, with each original fresh STA attaching before WPF creates any window. Keep xunit.runner.json appDomain=denied. Preserve original Show and Activate behavior and every keyboard-focus assertion; genuine activation then stays on the private desktop rather than taking the user's foreground.

The HWND-owner regression test verifies that an actual installer window belongs to the named private desktop. The native child-launch regression compares its desktop with the parent thread's desktop without OpenInputDesktop, which is unavailable on noninteractive window stations.

## Tried and ruled out
Cloaking on the user's desktop hid pixels but explicit Window.Activate still took foreground on hosted CI. Removing Activate left the first focus assertion null, so doing so would weaken real-focus coverage. A per-test private desktop could not close after focus because WPF retained text-services workers. One process-owned desktop plus appDomain=denied passed all 164 installer tests in Release without secondary-domain COM teardown.

## Watch out
This supersedes the earlier prohibition on private desktops for WPF: the failure was a lifetime mismatch, not rendering or focus capability. Do not run explicit dispatcher shutdown from a temporary UI thread or close the shared desktop between tests. Windows releases its single process-owned handle when the finite testhost process exits. Preserve source-backed original focus behavior; never remove coverage or add visible fallback just to make CI pass.
