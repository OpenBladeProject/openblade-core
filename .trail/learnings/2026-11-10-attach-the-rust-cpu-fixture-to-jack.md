---
title: Attach the Rust CPU fixture to its job at process creation
author: Jack
date: 2026-11-10
branch: feat/rust-phase-5l-cpu-worker
touches:
  - tools/openblade-windows-host/src/cpu_worker/windows.rs
  - tools/openblade-windows-host/src/cpu_worker/windows/native.rs
  - tools/openblade-windows-host/tests/cpu_worker_windows.rs
---

## Decided

Keep the Rust CPU transport behind the development fixture feature. Create
the process with a Win32 job-list attribute and allow only its three stdio
handles to inherit. Publish the overlapped parent pipes after successful
creation and registration. Close the job on retirement and allow replacement
only after the original process handle signals or creation explicitly failed.

## Tried and ruled out

The ordinary-user sandbox permitted one nested-job launch. An inert copy of
the Cargo fixture returned a Ready frame and synthetic 42 C sample, then the
retained process handle signaled on retirement. No breakaway or alternate
launch was needed. Scripted failures distinguish no child, failed
registration, a late launch, failed process wait, and a lost launch result.

## Watch out

A closed pipe, job close, timeout, or failed wait cannot prove exit. A
lost launch result also cannot prove absence. Keep observing a retained
handle after the short stop budget. Fixture evidence does not admit
an installed worker, PawnIO, live sensors, or a thermal policy action.
The approved Claude wrapper's native mode exposes the whole tracked
snapshot, so a patch-only source-scope review requires a wrapper change.
