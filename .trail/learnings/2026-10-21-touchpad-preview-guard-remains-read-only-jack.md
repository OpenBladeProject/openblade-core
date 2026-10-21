---
title: Touchpad preview guard remains read-only
author: Jack
date: 2026-10-21
branch: feat/touchpad-toggle
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
---

## Decided
The touchpad preview controller deliberately throws instead of sending input. Trail check reported this guard text as reintroducing the words input must send from the native INPUT geometry note. Source review confirms the guard prevents mutation and does not conflict with the Win32 layout requirement.

## Tried and ruled out
Do not remove the disconnected-preview guard to satisfy the lexical match. Keep the native union size assertions and the fake preview controller as separate contracts.

## Watch out
The keyword match is a false positive; the pull request records the exact rejected line and the reason for keeping it. Real touchpad mutation remains confined to an explicit Laptop-page action through fresh state read and bounded confirmation.
