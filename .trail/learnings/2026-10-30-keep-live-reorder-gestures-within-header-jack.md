---
title: Keep live reorder gestures within header-owned card groups
author: Jack
date: 2026-10-30
branch: fix/section-drag-preview
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/PageSections.cs
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray/App.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/PageLayoutPreferenceTests.cs
  - src/OpenBlade.Tray.Core/CardOrderPreference.cs
---

## Decided
Keep canonical page-section and dashboard ordering, but start whole-section moves only from explicit section headings. Register direct settings cards in their own invariant group; a nested card owns its bubbling pointer gesture and cannot drop into another group. Persist optional card orders with invariant XAML IDs while keeping hidden controls and lazy groups intact. Lighting Keyboard owns its heading and Windows Dynamic Lighting banner. Notifications is independently ordered and migrates next to Behavior with its former visibility.

## Tried and ruled out
Do not reorder every descendant Border or read-only comparison table. Explicitly mark direct settings-card membership and keep interactive Expander roots and fixed notices out of it. Moving only members to their old indices can shift fixed siblings; reconstruct the complete desired child order instead. Mirror the actual visual subtree into a hit-test-free root overlay at 50 percent opacity rather than rebuilding controls or showing an insertion line.

## Watch out
Native WinUI rejects Int32.MaxValue for Canvas.ZIndex; use a bounded overlay value and exercise preview creation in the compiled harness. Preserve the original pointer source through capture handoff so heading admission works for real gestures. Restore saved card orders without queuing partial preference writes, update shared last-row styles after moves, and stop late Loaded work after disposal. Software and disconnected UI checks do not establish touch-device behavior.
