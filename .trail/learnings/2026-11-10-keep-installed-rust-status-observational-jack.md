---
title: Keep installed Rust status observational
author: Jack
date: 2026-11-10
branch: feat/rust-installed-status-source
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_wire.rs
---

## Decided

Phase 9m exposes the installed-candidate GetStatus source behind the private ports. It projects paired registry identity, SetupAPI metadata, OS power and possible controller process names. A positive unmatched host is distinct from incomplete evidence. Commands stay blocked. Required v79 profile and intent fields carry the managed safe defaults. Emit the candidate build version and the unconfigured Windows power-mode associations required by the C# parser.

## Tried and ruled out

A PnP candidate, a PID and a process-only scan establish no control ownership, observed firmware values or absence of controllers. Do not load synthetic status payloads. A timed-out scan cannot publish late evidence. Tokio blocking tasks could delay SCM runtime teardown after a failed drain.

## Watch out

Use one process-local revision per changed projection. v79 encodes unavailable OS power as ExternalUnknown. Cancel scans cooperatively and acknowledge retirement only after joining. An uninterruptible OS call can outlive a failed drain until process exit. Listener admission and composition belong to the separate integration phase.
