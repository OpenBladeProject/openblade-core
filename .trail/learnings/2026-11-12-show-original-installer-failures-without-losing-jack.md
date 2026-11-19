---
title: Show original installer failures without losing recovery context
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

Send a bounded typed Failure event over the existing Setup progress pipe so the graphical installer can show the original step, primary error, and separate cleanup or recovery details. Keep the full exception log. Report neutral startup and explicit payload verification and CPU prerequisite steps. Preserve custom aggregate guidance about retained prior files.

## Tried and ruled out

Burn's generic exit-code 1 and discarded stderr do not identify a setup failure. Do not parse free-form log text to infer it. An async-void ApplyComplete continuation loses the original callback boundary; use a bounded synchronous drain, and queue engine logging/presentation so the pipe reader never needs the waiting callback. Skip the wait when no Setup writer is expected. Cancellation must not claim restoration or be masked by a package user-exit code.

## Watch out

Capture the failed package's current error before rollback events arrive, and keep the original Setup step frozen once recovery begins. Staging cleanup can fail before activation, so secondary wording must cover cleanup as well as recovery. Producer and consumer bounds and enum contracts need matching tests. Disconnected rendering and offline tests do not establish live install recovery.
