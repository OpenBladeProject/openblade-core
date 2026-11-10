---
title: Bind function-row actions to the held route and worker retirement
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6l-function-row-executor
touches:
  - rust/crates/openblade-platform-windows/src/function_row_hook.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/tests.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/executor.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/executor_tests.rs
---

## Decided

Phase 6l drains the bounded Windows hook queue through typed effects on its consumer. Use the Phase 6j domain cadence and managed FunctionRowActionExecutor as the oracle. The display usage has one native owner, with independent WMI routes for the two keys. Only positively established initial native no-submission selects WMI. Reset is part of retirement.

## Tried and ruled out

A general failure or unsupported status cannot select WMI. A possibly submitted release cannot be replayed during reset. An uncertain press can still require a distinct neutral release. A confirmed WMI level alone triggers OSD.

## Watch out

Keep native ownership latched on a failed release, and block handoff until both queue and cleanup finish. Cancellation skips queued Down and Repeat while permitting eligible Up and reset. The fixture does not establish real effect admission, completion classification, or current-user agent lifecycle behavior.
