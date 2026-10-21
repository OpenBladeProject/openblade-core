---
title: Windows touchpad state and explicit toggle contract
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
Use Windows 11 24H2 SPI_GETTOUCHPADPARAMETERS with V1 to read Precision Touchpad presence and enabled state. The touchpadEnabled field is system information, so SPI_SETTOUCHPADPARAMETERS ignores it. Change the setting with Microsoft's documented Ctrl+Win+F24 chord, after a fresh state query, and confirm with bounded readback. The Laptop page offers explicit current-user control without service IPC or firmware changes.

## Tried and ruled out
Do not write the read-only PrecisionTouchPad Status registry value or treat SPI_SETTOUCHPADPARAMETERS as an enable/disable API. Do not replay a toggle after an unconfirmed or partial submission. INPUT must include the full mouse union geometry even for keyboard-only SendInput (40 bytes x64, 28 x86).

## Watch out
The existing gaming-mode hook ignores injected input before key blocking. Reject held modifiers/chord keys and release only accepted unmatched keydowns after partial input. Keep the last confirmed switch presentation on query failures while disabling edits. Disconnected previews must use a fake controller and never send input; physical Windows handling remains separate validation.
