---
title: Match keyboard submenu styles to existing collapsible settings
author: Jack
date: 2026-10-31
branch: fix/keyboard-input-submenus
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
Keyboard input submenus must use the same expander and header styles, header spacing and typography as existing THX and Settings submenus. The shared header contract test includes FunctionKeyBehaviorExpander and SnapTapExpander and compares their content margins with the existing THX reference.

## Tried and ruled out
The multimedia-input row used the last-subcard corner style even though Gaming mode and other keyboard rows follow it in the same card. Use the regular SettingsNeutralSubcardStyle for that middle section; reserve the last-row variant for the final SnapTap child.

## Watch out
Test shared visual semantics and compare with an existing submenu, rather than only updating style-use counts. Keep ordinary subrows transparent at rest with their own hover feedback, and keep outer expander containers from owning that feedback. Reuse unchanged localization and backend validation evidence for this corner-style correction.
