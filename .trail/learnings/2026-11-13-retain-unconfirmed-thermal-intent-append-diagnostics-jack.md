---
title: Retain unconfirmed thermal intent append diagnostics
author: Jack
date: 2026-11-13
branch: fix/thermal-journal-diagnostics
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
---

## Decided
Remember a failed FirstIntent or SecondIntent replacement separately from cached progress. Genuine unresolved submission history remains first; unconfirmed intent publication precedes Prepared and local configuration causes. Retain pending/published bytes and require read-only reconciliation without replay.
## Tried and ruled out
A cached Prepared row cannot prove that no intent reached storage after an ordinary failed intent append. Do not settle, delete, retry or rewrite that evidence. KnownNotSubmitted failure is not an intent-append failure. Typed protection errors still propagate through the shared fatal latch.
## Watch out
The injected composed dispatcher/storage/native-retirement test covers partial pending, full pending and published-unconfirmed FirstIntent cuts with zero setters. Pending files refuse reopen; published [0,1] reloads as possible submission. These are injected tests; the real Windows storage suppliers are only source-inspected and compiled. Journal grammar, admission, IDs and hardware behavior are unchanged. Lexical Trail conflict reports about submission/intent/fatal words do not establish a contradiction; the focused controls preserve those gates.
