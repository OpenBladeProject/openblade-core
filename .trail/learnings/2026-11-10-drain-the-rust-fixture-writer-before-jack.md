---
title: Drain the Rust fixture writer before reporting SCM stopped
author: Jack
date: 2026-11-10
branch: feat/rust-protected-scm-runtime
touches:
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/configuration_writer/fixture.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm.rs
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/windows/scm/tests.rs
---

## Decided
Run the existing development foreground host and read-only native SCM mode through the same status adapter and startup/stop coordinator. Admit the protected fixture and claim the private listener before publishing RUNNING. Join pipe work and drain any accepted profile transaction before releasing the endpoint claim and writer authority.

## Tried and ruled out
Dropping the Tokio runtime eventually waits for blocking work but is too late for terminal status. Reopening an interrupted Temp fixture while its owner holds its directory pins can fail with a sharing violation before the marker is inspected.

## Watch out
The scripted SCM boundary exercises local callbacks and status mapping. It does not prove native callback delivery or a service account's protected NTFS and private-pipe admission. Keep native service activation in a separately authorized disposable environment.
