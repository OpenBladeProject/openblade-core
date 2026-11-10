---
title: Exercise the Rust application reporter on the private v79 pipe
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6r
touches:
  - tools/openblade-windows-host/src/windows/reporter_tests.rs
  - tools/openblade-windows-host/src/application_runtime.rs
---

## Decided

Use a Windows test-only current-user caller against the unified fixture's real private pipe. The existing endpoint verifies peer identity and the unified fixture owns scripted session authority. Preserve the v79 required nullable path and correlate each result to its request before completing its sequence.

## Tried and ruled out

The first scoped step writes and confirms Performance. The scripted battery transition only selects intent, so returning to full power can already have that preset confirmed. Assert the scoped outcome and the independently exposed desired and confirmed values at both steps.

## Watch out

A canceled connection may already have reported context. Reconnect with current evidence and a fresh sequence. An old pipe reply can only complete its own sequence. Reports do not supply authority to clear a pending write. Native agent startup, observation delivery, and shutdown remain outside this fixture.
