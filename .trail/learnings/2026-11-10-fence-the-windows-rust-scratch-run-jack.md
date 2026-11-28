---
title: Fence the Windows Rust scratch run marker
author: Jack
date: 2026-11-10
branch: feat/windows-rust-run-marker
touches:
  - tools/openblade-windows-host/src/configuration_writer/protected.rs
  - tools/openblade-windows-host/src/configuration_writer/scratch.rs
  - tools/openblade-windows-host/src/configuration_writer/scratch/run_marker.rs
  - tools/openblade-windows-host/src/configuration_writer/scratch/run_marker/format.rs
  - tools/openblade-windows-host/src/configuration_writer/scratch/run_marker/tests.rs
---

## Decided
The opt-in Windows Rust run marker shares the existing enrolled scratch configuration writer lease. Ordinary startup classifies an uncleared marker using strict wall-clock and uptime proof. Explicit handoff independently verifies the outgoing backend generation and requires both marker and temp to be absent.

## Tried and ruled out
Staging with write and delete access alone denied regular-file metadata verification under a normal token. The stage now requests read access too. Cleanup with reader sharing can leave the marker delete-pending. Use an exclusive delete handle and verify absence before releasing the writer lease.

## Watch out
Windows .NET 10 emits CRLF in the managed indented schema-v2 fixture. Preserve the fixed interrupted temp and withhold cleanup authority when publication cannot be confirmed. The scratch marker and lease supply no retirement proof and leave the child retirement protocol unchanged.
