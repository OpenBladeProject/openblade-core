---
title: Nest keyboard input methods under their parent controls
author: Jack
date: 2026-10-31
branch: fix/keyboard-input-submenus
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.KeyboardBackends.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
  - src/OpenBlade.Tray/Strings/zh-CN/Resources.resw
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
Keep Multimedia key input inside Function key behavior and SnapTap input inside SnapTap (A/D), using native collapsed Expanders and short localized subtitles. The function-key section lives in the shared keyboard template so Dashboard and Laptop agree. Track loaded template instances and synchronize their input selectors under the existing initialization guard; unregister unloaded instances.

Use shared expander-container row styles derived from normal/last settings rows, with zero padding and pointer feedback disabled on the outer container. The shared expander header and neutral child subcard own their own hover feedback.

## Tried and ruled out
Keeping input selectors as independent reorderable rows separates them from the settings they control. Leaving SettingsCardPointerVisual enabled on an outer Border around an Expander lets bubbled pointer events shade the complete header and child block. A named control moved into a DataTemplate cannot remain a single MainWindow field.

## Watch out
Preserve the input preferences, explicit setup actions, existing status text, and all backend/model admission. This is presentation and template-lifetime wiring only. Exact shared-style semantics matter more than usage counts. New subtitles must retain existing translated names for Windows hook and Razer driver. The keyboard-input snapshot environment switch is developer-harness-only and expands the sections without touching installed processes or hardware.

The current trail check's version, backend, StackPanel, resource and Expander lexical matches do not identify actual admission or ownership changes: the product patch increment is required, the existing backends remain unchanged, direct reorder membership remains on outer Borders, and nested subrows carry no independent CardId.
