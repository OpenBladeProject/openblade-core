---
title: Close live reorder lifetimes and verify Claude candidates
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - AGENTS.md
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
---

## Decided
Close live reorder lifetimes at the shared behavior: disposed is terminal, failed initiation ends the contact and releases its actual capture owner, and detached or hidden sources cancel before transforms. Touch hold commits through CancelDirectManipulations before capture; false means no action and is not a denial. Read the app's typed SystemAccentColor for insertion feedback. Keep reference identity explicit with a class constraint on registered options. Runtime checks cover custom accent, source removal/restored opacity and late registration after disposal.

## Tried and ruled out
Claude Opus5.5/high reviewed87e01298 and returned9 explicitly unconfirmed candidates. Confirmed5 source/lifetime defects; do not implement the PointerId-collision or threshold-halving suggestions. Windows guarantees unique IDs for live pointers and its native drag algorithm uses the full metric on each side. A read-only host probe returned4 pixels for both drag metrics at96/144/192 DPI; generic DPI guidance did not establish a metric-specific defect. Current options are classes, so boxed-struct failure was not a reachable production bug. Duplicate dictionary lookups are bounded, without an established performance regression.

## Watch out
Preserve ordinary scrolling before hold and normal mouse capture handoff. Native touch remains source-reviewed, not physically validated. A capture can be acquired before admission fails, so cleanup must not depend only on dragging being set. Queued MainWindow refreshes can outlive event unsubscription; Register must not revive a disposed instance. Never call a generated review candidate confirmed without checking current source and contracts.
