---
title: Merge the native SCM fixture with the integrated Windows host
author: Jack
date: 2026-11-10
touches:
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm/vm.rs
  - tools/openblade-windows-host/vm-client/Program.cs
  - docs/windows-rust-phase5j-vm.md
---

## Decided
Keep the native SCM fixture in its own required-feature binary. Reuse the integrated private host's shared pipe module and disable application replay for the VM scenario. Create the managed staging directory with the protected descriptor at creation so the newer Setup helper can verify its existing owner.

## Tried and ruled out
The fixture parent uses the old pipe module layout and replay signature. A plain PowerShell directory creation does not bind the owner before the newer Setup admission runs. The ordinary setup rehearsal stays pinned to the original reviewed pair.

## Watch out
The native binary and managed client build offline, and injected lifecycle and admission tests can run without SCM. Real SCM checkpoints, LocalSystem peer verification and cleanup still need an authorized disposable VM. The default-base Trail check scans inherited candidate history and flags unrelated wording; compare the integration against its exact first parent.
