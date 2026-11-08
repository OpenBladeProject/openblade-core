---
title: Global search uses canonical rows and current WinUI queries
author: Jack
date: 2026-11-06
branch: feat/global-settings-search
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/LocalizedXamlResourceLoader.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
  - src/OpenBlade.Tray/Strings/zh-CN/Resources.resw
---

## Decided

Index localized pages and canonical settings anchors in the tray. Use a card ID or named template child for the actual row and keep availability checks on the visible path and model-facing view model. This preserves section customization and avoids indexing dashboard copies. Share the native result template between inline and compact search.

## Tried and ruled out

An AutoSuggestBox length limit belongs on its TextBoxStyle. A SymbolIcon cannot use the shared FontIcon style. A compact snapshot must lay out the flyout before opening its suggestions, and the window capture and attached suggestions are separate RenderTargetBitmap targets. Scale AppWindow.Resize dimensions from DIPs using the current XamlRoot.

## Watch out

WinUI delays AutoSuggestBox UserInput notifications. Gate suggestion updates on the active, focused editor, refresh a dirty query on focus, and validate a chosen result against the submitted query before opening it. Use native compact flyout focus restoration. Gate unrealized template targets with the same view-model predicates as their XAML, then check live visibility after realization. Search activation navigates only.
