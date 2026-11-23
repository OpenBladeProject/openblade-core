---
title: Native Agent display automation port
author: Jack
date: 2026-11-23
branch: feat/native-agent-display-automation
touches:
  - tools/openblade-windows-host/src/session_native/display_query/automation.rs
  - tools/openblade-windows-host/src/session_native/display_query/recovery/automatic.rs
  - tools/openblade-windows-host/src/session_native/display_query/write.rs
  - tools/openblade-windows-host/src/session_native/display_query/automation_vectors.rs
  - tests/OpenBlade.Session.Tests/DisplayAutomationCoordinatorTests.NativeVectors.cs
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
---

## Decided
- Native display automation (`agent-display-automation-v1`, marker `session-display-automation-v1`) ports managed `ServiceDisplayAutomationLoop` + `DisplayAutomationCoordinator`. The coordinator state lives in `display_query/recovery/automatic.rs` inside the same `Recovery` that owns the durable checkpoint, so manual begin/complete/explicit read and automation share one lock and one record. The runner (`display_query/automation.rs::serve`) is one scheduled slot plus a `Notify`; it runs one transition at a time in `spawn_blocking`, and stop cancels and joins it.
- Automatic writes go only through `profile_apply::transition` under the display operation `Gate` that also serializes every manual query and write. A manual mutation calls `write::manual_turn`, which preempts (cancels) the running transition before waiting for the gate, the way managed `BeginManualOperation` runs before the applier's gate.
- Scheduling never invents power admission: `advance_to` filters `PowerAdmission::advance()` to the target's source, and without one the target stays pending with the context marked lost, so the next confirmed status of that source schedules it.
- Both package readers refuse the feature and the marker by name, like function-row.
- Managed parity lives in `tools/openblade-windows-host/tests/data/display-automation-vectors.json`, recorded by `DisplayAutomationCoordinatorTests.NativeAutomationVectors` (Session.Tests) and replayed by `display_query/automation_vectors.rs`. Its `DIFFERENCES` table names the only native difference: `mid-apply/lock-resumes-after-unlock` (managed resumes on its own power context while the status is unconfirmed; native waits for the next confirmed status).

## Tried and ruled out
- Running the managed vector fake synchronously: the transition then runs inline under the coordinator's reentrant lock, and a "during apply" trigger re-enters `Observe`. The fake yields first in Query and Apply so transitions run off the lock, as over the real applier.
- A vector case with a manual operation during an automatic apply: native begins the manual operation only after the canceled transition records its result (the gate), so the order differs by design; it is covered by Rust unit tests instead.

## Watch out
- Mutation testing: the `self.last == Some(latest.source)` guard in `complete_transition` survives; `last` and `observed` change together under the lock, so it differs only if a manual write begins between the watcher's power admission and its automation observation.
- The managed `DisplayAutomationCoordinatorTests.cs` working copy was LF while the rest of the project was CRLF; with autocrlf the index is LF either way.
