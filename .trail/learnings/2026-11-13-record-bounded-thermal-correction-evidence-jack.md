---
title: Record bounded thermal correction evidence
author: Jack
date: 2026-11-13
branch: fix/native-thermal-admission-fixtures
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tests/OpenBlade.Service.Tests/NativeThermalEdgeFixtureTests.cs
---

## Decided
Keep the tested source snapshot a26da85c11c396646bc99b3a2f772e0a635927de distinct from this documentation-only addendum. All 22 prelisted injected native controls passed. Nine native and eight managed compatibility builds passed offline. The 153 captured native frame strings passed the final managed strict codec. Service opt-in evidence is reused through the complete 908-input equivalence receipt, with metadata-only differences stated explicitly.

## Tried and ruled out
Exact predecessor-body binding caught an unintended unreachable branch in an old test; it was removed before the final native run, preserving all 59 literal predecessor bodies. Missing and invalid explicit fixture inputs failed as required, while omitted opt-in was a real skip. A config-only Stored operation has no submission journal and must not inherit FirstIntent uncertainty wording. No broader runtime, hardware, installed-service or backend-completion claim follows from these injected checks.

## Watch out
Preserve actual ProfileStore fixture bytes, including CRLF and no final newline. Keep phase-end compiler/input captures distinct from a hermetic invocation snapshot. The original decision note remains unchanged; this append-only addendum supplies the required ruled-out section and final evidence boundary. Finite transaction lifetime, managed handback and the larger Windows replacement remain unfinished.
