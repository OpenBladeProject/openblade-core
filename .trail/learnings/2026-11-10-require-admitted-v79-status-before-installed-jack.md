---
title: Require admitted v79 status before installed Rust service readiness
author: Jack
date: 2026-11-10
branch: feat/rust-installed-service-readiness
touches:
  - tools/openblade-windows-host/src/windows/scm/installed.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_wire.rs
---

## Decided
Phase 9g drives a bounded v79 GetStatus round trip behind the installed candidate boundary. First-instance ownership and the managed ACL must be attested by the listener; verify the server PID on the same client handle before and after the exchange. Resolve the authenticated caller after the first frame, then request a typed snapshot from the admitted runtime for that caller.

## Tried and ruled out
Do not use the development host or VM status fixture as an installed source. The native adapter fails closed until the secure listener and real runtime source can both be supplied. Phase 9d owns the managed Setup probe; Phase 9f owns protected image admission.

## Watch out
Keep the accepted probe handle live through the client's final PID check. On STOP during startup, signal cancellation and wait for both listener retirement and source drain. After running, a listener or source failure must leave Running before drain; an unconfirmed drain must never be reported as a clean stop.
