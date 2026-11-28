---
title: Gate the Phase 5j native SCM fixture inside a disposable VM
author: Jack
date: 2026-11-10
branch: test/rust-phase-5j-native-scm-fixture
touches:
  - tools/openblade-windows-host/src/windows/scm/vm.rs
  - tools/openblade-windows-host/src/windows/fixture_admission.rs
  - tools/openblade-windows-host/src/configuration_writer/protected.rs
  - tools/openblade-windows-host/vm-client/Program.cs
  - docs/windows-rust-phase5j-vm.md
---

## Decided
- Use one opt-in x64 developer image at a fixed protected VM location. Reuse the native SCM callback adapter and the existing synthetic protected runtime and managed service client.
- Publish a second START_PENDING checkpoint after admission and before the private listener can exist. Gate the renderer independently of power steps and keep the writer and endpoint claim until cleanup settles.

## Tried and ruled out
- The injected lifecycle reporter can verify ordering and uncertain retirement offline. Native ServiceMain, LocalSystem, and the real SCM status transitions need a separately authorized clean VM.
- An exclusive final marker handle can race the runtime poll. Publish an empty leaf with its final protected ACL and a share-compatible creation handle.

## Watch out
- A START_PENDING PID is not authoritative. Bind callback arguments and fixed registration, then retain and independently match the RUNNING PID to the named-pipe peer in the VM.
- Keep the Setup-derived ACL and ordinary identity denial gates, exact artifact hash, separate live authorization and no-replay cleanup.
