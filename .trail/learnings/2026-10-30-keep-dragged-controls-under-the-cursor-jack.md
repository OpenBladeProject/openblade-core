---
title: keep dragged controls under the cursor with provisional reflow
author: Jack
date: 2026-10-30
branch: fix/section-drag-preview
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray/App.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
---

## Decided
Dragging uses the actual live card or whole header-owned section at half opacity, attached to the original cursor grip. Move neighboring controls provisionally; mutate the canonical collection once on release. Escape, capture loss, invalid drop, visibility/membership changes and disposal restore presentation without saving. Keep fixed notices in their existing child slots and preserve hidden canonical siblings.
Use a private TranslateTransform after the existing RenderTransform. Restore the original binding/local value/unset state and sibling Z-order. Animate only neighbors for 120 ms and honor Windows animation settings. Cursor coordinates belong to the XamlRoot, converted into the host before applying offsets.

## Tried and ruled out
The earlier composition mirror left a second original behind. Moving the actual source avoids an undocumented assumption about mirror-root opacity.
UIElement.Translation/TranslationTransition work on ordinary cards but throw on NavigationView items after the native template acquires a handout visual. Compose RenderTransform instead; do not take over the native composition animation.
Checking only captured rows misses a newly visible trailing card. Compare current peer/member counts and source registration identity too.

## Watch out
ShowNoninteractiveSnapshotWindow disables root XAML hit testing. The isolated reorder harness must explicitly enable it while keeping native no-activate/input guards; otherwise hit-test assertions can pass vacuously or return no targets. Keep fixtures within root bounds at Windows text/display scaling.
Use normal front-hit ordering for overlay blocking, with spatial z-order fallback for null-background host vacancies. Enforce host bounds. Source-slot insertion thresholds use proposed positions rather than animated hit bounds to prevent oscillation.
Reuse the fixed header/card membership established by the earlier section-drag learning. This remains tray-only work; preserve installed service and Session Agent binaries.
