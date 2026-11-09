---
title: Explain keyboard input choices with recommendations and game caveats
author: Jack
date: 2026-10-31
branch: fix/keyboard-input-guidance
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.KeyboardBackends.cs
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/zh-CN/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
For multimedia-key input and SnapTap input, recommend trying Razer driver first and switching manually to Windows hook if it does not work. Include the relevant game caveat in that subtitle: some games may register function keys twice, while SnapTap may not work in some games. Keep English, Chinese and Russian aligned.

## Tried and ruled out
Built-in versus external keyboard scope and driver-installation comparisons did not tell average users which option to try. A second conditional warning repeats the same game caveat, so keep the limitation once in the subtitle and remove its redundant presentation-only resource and visibility plumbing.

## Watch out
These subtitles guide a manual choice. They do not change defaults, saved preferences, driver setup, backend recovery or automatic fallback. Keep existing shared subcard styles, wrapping and expander behavior. Review translations independently and inspect expanded native renders after changing the text length.
