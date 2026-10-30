---
title: Reject outgoing reorder visuals retained by removal transitions
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - AGENTS.md
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
---

## Decided
A dragged card is admitted only while it remains in its actual host, not merely while IsLoaded and its visual ancestry still reach the root. The removal regression for PR #861 proved that AddDeleteThemeTransition retains the old visual parent after Panel.Children.Remove. Check Panel.Children membership along panel edges and ItemsRepeater.GetElementIndex for realized repeater children before source/target admission.

## Tried and ruled out
Root ancestry alone did not fix the new detached-card regression: an outgoing transition visual could still be treated as movable. Do not wait for Unloaded, add timing sleeps, or remove normal transitions to pass the test. EndDrag still occurs before intentional collection reordering, so the new guard does not reject the completed move.

## Watch out
The regression must check both move refusal and exact source-opacity restoration immediately after removal. NavigationView uses ItemsRepeater template children, not only panels; keep its supported realized-index check. Native mouse verification remains separate from the disconnected helper smoke.
