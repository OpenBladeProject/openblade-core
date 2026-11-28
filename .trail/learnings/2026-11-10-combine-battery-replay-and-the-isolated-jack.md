---
title: Combine Battery replay and the isolated SCM fixture
author: Jack
date: 2026-11-10
touches:
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm.rs
  - tools/openblade-windows-host/vm-client/Program.cs
  - docs/windows-rust-phase5j-vm.md
---

## Decided
Keep the pinned Battery/Setup and native SCM fixture heads as the two merge parents. Retain the Battery SDK features alongside the separately required vm-scm-fixture binary, plus protected creation of the managed staging directory before sealing it.

## Tried and ruled out
Trail check against the Battery parent matched only prose in the inherited SCM documentation. The runbook keeps separate disposable VM authorization, the protected staging and marker gates, a shared bounded shutdown window, and independent verification of writer and renderer release. The host workspace remains under tools. The ordinary binary selection cannot enter the dedicated VM registration route.

## Watch out
The shared native host and writer code changed, so compare ordinary role build hashes and retest signed scratch readiness and rollback against the rebuilt pair. Keep replay acquisition inert and do not substitute the SCM or GPU close fixture for an ordinary role. Native SCM, LocalSystem and cleanup observations still require the separately authorized VM.
