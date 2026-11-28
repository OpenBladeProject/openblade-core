---
title: Merge protected service image admission with v79 readiness
author: Jack
date: 2026-11-10
branch: test/rust-phase-9j-integration
touches:
  - tools/openblade-windows-host/src/installed_main.rs
  - tools/openblade-windows-host/src/scm_host.rs
  - tools/openblade-windows-host/src/service_frame.rs
  - tools/openblade-windows-host/src/windows/scm/installed.rs
  - tools/openblade-windows-host/src/windows/scm/installed/native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_wire.rs
  - tools/openblade-windows-host/src/windows/scm/installed/tests.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
Phase 9j keeps the shared protected-image known-folder and exact path definitions and replaces the old native endpoint stub with the Phase 9g typed readiness driver. Keep launch admission ahead of preparation and preserve bounded retirement on STOP or failure.

## Tried and ruled out
A known-folder path, SCM registration and PID do not prove the process loaded the retained protected file generation. Keep fixture data at the test boundary. The Trail check flags inherited wording in service_frame.rs and readiness.rs. Service framing is bounded IPC, the readiness probe is read-only v79 GetStatus, and retirement waits for listener shutdown and runtime drain.

## Watch out
Phase 9i owns authenticated role hashes. Before native startup can expose the endpoint, it still needs independent proof of the loaded generation tied to the retained image, plus controller and ownership admission, a secure listener and an admitted real v79 status source.
