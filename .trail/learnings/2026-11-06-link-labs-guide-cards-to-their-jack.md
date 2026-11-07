---
title: Link Labs guide cards to their original Lighting controls
author: Jack
date: 2026-11-06
branch: feat/labs-sidebar
touches:
  - CHANGELOG.md
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/keyboard-lighting-file-format.md
  - docs/ru-RU/keyboard-lighting-file-format.md
  - docs/zh-CN/keyboard-lighting-file-format.md
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
---

## Decided
Use Labs as an always-visible guide to experimental controls. Keep the original Keyboard heading and link its guide cards to the working controls on Lighting, where import progress and the profile actions already live. Resolve the destination within Lighting's own template instance, focus enabled controls, and bring the target into view after the page is laid out.

## Tried and ruled out
Moving working controls into Labs left preset import separated from progress and Save to Profile. Keep the Lighting destination usable even when a linked control is disabled. The same UI and service gates still decide whether a change can be made.

## Watch out
Do not equate an empty capability list during discovery or loss of connection with confirmed lack of 60 Hz support. Validate the actual navigation action and viewport in the disconnected harness. Localized WinUI resource keys must match their XAML property, even when the sidebar and page title share wording.
