---
title: Scope native brightness Trail matches to the accepted base
author: Jack
date: 2026-11-14
branch: feat/native-agent-owned-brightness
touches:
  - ".gitattributes"
  - rust/crates/openblade-platform-windows/src/brightness.rs
  - rust/crates/openblade-platform-windows/src/brightness/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/native.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/native/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker/tests.rs
  - tools/openblade-windows-host/src/session_native.rs
---

## Decided
Check this native Agent brightness increment against accepted a8d567643e87be3eb0776b9d39bdb9f98555a4d1 with trail check --base, not the current default branch. The default check includes hundreds of inherited migration files and clips at 400KB. Incremental check reports lexical overlaps too; preserve its output and triage actual source contracts before committing.
## Tried and ruled out
The incremental check's twenty reported matches do not establish a behavior conflict. Query-only target/row projection keeps setter admission separate; signed suffix matching supplies observations, never mutation confirmation. The actual installed query uses owned native WMI, awaits thread/COM destruction and both feature drains, and emits available/unavailable frames accepted by the managed codec. This follows the historical no-detached-WMI, no-fake-response and no-false-completion learnings. GPU/controller, LampArray and mutation-envelope phrase matches refer to untouched contracts. The generated learning itself can match new code on words such as query, setter and count.
## Watch out
Do not suppress or erase learnings to obtain exit0. Record the matched title/quote and source reasoning in the small triage report and commit message. A nonzero lexical check is not a passed check; report it as triaged. Offline injected success still does not prove physical or installed-role behavior. Root owns the subsequent frozen Claude review and acceptance.
