---
title: Describe Windows hook game caveats as extra actions
author: Jack
date: 2026-10-31
branch: fix/keyboard-input-guidance
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/zh-CN/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
---

## Decided
Describe the Windows-hook multimedia-key caveat as extra function-key actions in some games. Keep the Razer-first manual-selection guidance. This corrects the function-row caveat recorded in the earlier keyboard recommendations learning: do not describe two identical function-key presses.

## Tried and ruled out
The Windows low-level hook maps an intercepted press to one FunctionRowInputEvent and returns suppression for the original low-level key. A game input path may still see the original function key alongside its multimedia action. That supports an extra game action, not a claim that OpenBlade emits two original function-key events. Review source semantics before adopting a user's illustrative symptom as a factual event claim.

## Watch out
Use plain wording: some games may trigger extra function-key actions. Chinese and Russian must preserve that distinction. The separate SnapTap caveat remains that it may not work in some games. No input behavior, backend selection or safety admission changes are needed for this text correction.
