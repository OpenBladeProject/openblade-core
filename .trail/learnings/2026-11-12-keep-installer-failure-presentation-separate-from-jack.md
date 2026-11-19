---
title: Keep installer failure presentation separate from lexical policy matches
author: Jack
date: 2026-11-12
branch: fix/installer-failure-details
touches:
  - Directory.Build.props
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs
  - build/installer/OpenBlade.Installer.UI/InstallerProgressPipe.cs
  - build/installer/OpenBlade.Installer.UI/InstallerWindow.xaml
  - build/installer/OpenBlade.Installer.UI/OpenBladeBootstrapperApplication.cs
  - build/installer/OpenBlade.Installer.UI/Properties/Resources.qps-ploc.resx
  - build/installer/OpenBlade.Installer.UI/Properties/Resources.resx
  - build/installer/OpenBlade.Installer.UI/Properties/Resources.ru-RU.resx
  - build/installer/OpenBlade.Installer.UI/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Setup/Program.cs
---

## Decided

The installer failure-display change keeps the Trail checker unchanged and records its nonzero lexical gate in the commit and PR. Its matches do not identify a policy conflict: 0.58.7 to 0.58.8 is a required patch bump; copy/timeout strings are disconnected installer fixtures; CPU driver wording identifies the existing prerequisite and admits no new driver; cleanup/recovery text does not change firmware restoration.

## Tried and ruled out

The gate also matches this task's own statement that a generic exit-code 1 does not identify a failure. Showing the typed failing step and error implements that decision. The two-second connected-pipe completion bound is not a repeated temperature-sampling delay. Scrollable WPF failure text is not a disabled settings StackPanel.

## Watch out

Do not edit older learnings or weaken the gate to suppress lexical matches. Keep model admission, low-power ordering, sensor deadlines and hardware authority separate from this installer presentation change. Preserve nonzero gate status and the exact behavioral checks in the PR.
