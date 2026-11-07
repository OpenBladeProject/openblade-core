---
title: Retire global search with the localized window content
author: Jack
date: 2026-11-06
branch: feat/global-settings-search
touches:
  - src/OpenBlade.Tray/MainWindow.GlobalSearch.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
---

## Decided

Retire the global search keyboard accelerator, event handlers, flyout content and title-bar slots on permanent MainWindow disposal. Keep the search controls intact during temporary host detachment so a failed language change can reattach the previous view.

## Tried and ruled out

Closing suggestion popups and stopping the arrival timer during host detachment alone left 30 localized controls or roots alive in the disconnected resource audit. The visible and offscreen resource workloads also found the retired main view alive. Explicit search teardown passed all three audits on the same host. That combined change does not isolate one retaining native reference.

## Watch out

The fast XAML startup and isolated search captures cannot establish view reclamation. Run the existing language-change and both resource-retirement workloads when changing the title-bar search lifetime. Preserve cached native projections and avoid forced GC in production.
