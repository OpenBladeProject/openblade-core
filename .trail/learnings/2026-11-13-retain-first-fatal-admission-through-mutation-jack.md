---
title: Retain first fatal admission through mutation completion
author: Jack
date: 2026-11-13
branch: fix/native-thermal-edge-corrections
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
---

## Decided
Retain the first typed fatal admission cause in shared Runtime state across low-level Option-valued permit results, callback settlement, cancellation and later policy projections. Each permit and policy closure shares that non-consuming state. New callers and SID changes check it before factory or policy registration, so a later healthy Fresh check cannot downgrade or replace the original cause. Resolve and record capture the typed class before Option conversion. Observe and stop errors feed the same latch.

Always attempt drain, but preserve the original typed worker error when unconfirmed stop also returns a generic error. Keep the worker and original permit retained when drain is unconfirmed; do not claim custody retirement. A fatal FirstIntent after valid Prepared must not trigger terminal settlement replay. Ordinary expected refusal and local durable settlement uncertainty keep their existing separate meanings.

## Tried and ruled out
The initial frozen candidate passed55 focused native cases but missed a terminal-only typed fatal with healthy Fresh admission. The new regression failed on the preserved pre-fix harness with an ordinary Blocked reply, then passed after correction. Separate composed controls cover FirstIntent after valid Prepared, a genuine worker-returned fatal with unconfirmed retirement, and canceled mutation followed by framed Get, Observe and a new-SID setter requiring the same cause. All three corrected methods passed before final freeze. Rechecking health alone cannot reconstruct an error class already discarded by .ok().

## Watch out
These are offline pure/injected tests and cached compiler evidence, not installed or hardware validation. Preserve failed and intermediate generations and bind final exact-head inputs. Existing learnings are immutable: this correction is a separate new note. Sustained finite-ID lifetime,1MiB journal checkpointing, managed handback, capabilities/WinUI, native Agent/helpers, model/control parity, automation and authorized runtime/cutover validation remain unfinished.
