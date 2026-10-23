---
title: Native captions follow the resolved XAML theme
author: Jack
date: 2026-10-22
branch: fix/840-caption-theme
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
Native caption-button themes must follow MainWindow Root.ActualTheme, like Mica, rather than resolve Follow Windows independently through UseDefaultAppMode. Synchronize at construction, after ApplyTheme, and on ActualThemeChanged; unsubscribe during disposal. Keep system-owned caption colors and interaction states.

## Tried and ruled out
Explicit caption foreground colors are unnecessary for the demonstrated theme mismatch and would take ownership of inactive, hover, pressed, close-button and high-contrast behavior. Source-contract tests alone do not establish runtime synchronization; the developer-only caption smoke exercises real WinUI theme changes on the isolated test desktop.

## Watch out
Issue #840 shows white native caption glyphs on a light title area with HDR enabled. HDR as the trigger and recovery on that reporter's monitor remain unconfirmed. RenderTargetBitmap snapshots of MainWindow.Content exclude native caption glyphs; checking PreferredTheme is runtime configuration evidence, not HDR pixel validation.
