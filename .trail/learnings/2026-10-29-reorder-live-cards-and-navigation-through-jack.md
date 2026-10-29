---
title: Reorder live cards and navigation through shared pointer capture
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - src/OpenBlade.Tray/App.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
---

## Decided
Live card and primary-menu ordering moves canonical ObservableCollection options, preserving hidden state and the existing preference observers. Keep Dashboard first and About in the footer. Share one in-app pointer behavior across dashboard cards, customizable page sections and primary menu items. Begin on passive headings/backgrounds after the system threshold; interactive descendants retain input. Touch uses hold so normal page scrolling can continue.

## Tried and ruled out
CanDrag's automatic recognizer did not start on native NavigationView item text because its presenter handles and captures the press. The native fixture established that explicit source-subtree capture handoff after the threshold fixes menu dragging. The helper-only runtime smoke did not reveal this; a real UI-only pointer fixture did. Keep the fixture disconnected from MainViewModel and hardware/service clients. Launch the existing LocalizationPreview executable for computer-use validation; the ordinary Tray executable can be confused with the installed app by the targeting runtime.

## Watch out
Capture loss during the synchronous handoff must not clear the pending gesture. Capture only the active pointer, within the source subtree. Resolve drop targets from the front hit's ancestor chain, honoring scroll clipping, overlays and window bounds. Clear insertion feedback, restore original opacity and release capture on cancel, unload, Escape and disposal. Native mouse evidence covers menu reorder, a card's first drag after slider adjustment, and button clicks; touch remains source-reviewed until tested on a touch device.
