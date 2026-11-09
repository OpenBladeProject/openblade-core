---
title: Preserve keyboard ownership and unknown Fn state across backend changes
author: Codex
date: 2026-10-30
branch: feat/keyboard-input-backends
touches:
  - src/OpenBlade.SessionAgent/FunctionKeyObservation.cs
  - src/OpenBlade.SessionAgent/SnapTapProcessCoordinator.cs
  - src/OpenBlade.SessionAgent/SnapTapWindowsOwnershipJournal.cs
---

## Decided
Backend changes transfer ownership only after the previous owner proves cleanup. Keep independent A/D and function-row recovery barriers so disjoint Windows SnapTap and driver function-row input can coexist. Capture one immutable driver feature mask before admission, status publication, ownership flags and child launch.

A Windows A/D helper creates one durable lifetime marker before installing its hook, retains it after failure or crash, and removes it only after confirmed clean shutdown. No journal I/O runs on a key callback. Cached startup inspection blocks Windows function-row handling after uncertain Fn-driver ownership; legacy A/D-only journals remain disjoint. Existing driver journals now include optional feature scope, defaulting absent metadata to legacy SnapTap.

## Tried and ruled out
A process-exit flag alone does not establish transfer authority, and independent coordinators' local failure latches do not protect the other backend. Never let selecting a new backend bypass an unresolved journal. Table snapshots use the actual 292-byte hook ABI and 256-record bound, including legacy baselines.

A newly created Fn observer has no confirmed released state. Observe only the exact admitted model's real Fn report, invalidate knowledge after malformed Fn input or source reset, and read known/pressed as one nullable atomic snapshot per input. Unknown Fn state forwards the plain F key and shows a prompt to press Fn once; it does not disable A/D processing. After an action failure, reject further queued Down/Repeat actions synchronously while retaining permitted Up/reset cleanup.

## Watch out
Keep physical validation separate from offline state/ownership tests. Inspect real pipeline seams and native structure sizes, not only helpers that mirror expected outcomes. Windows input is global where documented; driver function-row support remains exact-model and experimental. The disconnected reorder smoke performs nine layout/capture passes: measured baseline 27 seconds and feature 29 seconds explain why its former 30-second process budget was fragile. Preserve all assertions/captures and phase diagnostics when adjusting that fixture's budget.
