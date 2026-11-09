---
title: Rehearse Windows control and matrix ownership through a fenced process handoff
author: Jack
date: 2026-11-09
branch: feat/rust-windows-owner-handoff
touches:
  - docs/windows-backend-replay.md
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/tests/standard_transport.rs
  - tools/openblade-windows-host/tests/standard_transport/ownership.rs
  - tools/openblade-windows-host/tests/standard_transport/ownership_windows.rs
---

## Decided
Use a Windows-only, test-target handoff between a fake incumbent and a single
Rust query process. Flush a fail-closed restart block before retiring control
and matrix, grant once over a private child pipe after job containment, then
require terminal receipt, the retained child process's exit and independent
read-only reconciliation before returning ownership.

## Tried and ruled out
A process-local command semaphore orders commands but cannot drain the matrix
worker, retire opened handles, or stop reacquisition. A PID, successful open,
child exit alone or timeout alone cannot clear a pending handoff. A job can
terminate an orphan but cannot establish firmware completion.

## Watch out
The test uses Windows process/handle/file semantics with scripted authority and
fake I/O. The real service needs a latch across all control and matrix openers,
service-protected restart recovery, explicit handle inheritance, strict fresh
authority and native retirement before the separately authorized physical query.
