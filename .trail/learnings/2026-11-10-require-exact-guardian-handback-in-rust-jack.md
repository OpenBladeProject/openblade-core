---
title: Require exact guardian handback in Rust replay
author: Jack
date: 2026-11-10
branch: feat/windows-rust-guardian-replay
touches:
  - tools/openblade-windows-host/src/fan_stop_guardian.rs
  - docs/manual-fan-stop-replay.md
---

## Decided
Phase 5q models the service side of guardian ownership with scripted effects.
Require the authenticated exact owner, increasing live revisions, owner exit,
matching durable terminal result and reservation archive before lease release.
A never-submitted owner refusal is Ordinary; verified cleanup does not prove
Stop happened.

## Tried and ruled out
A cached Automatic read, successful Auto request, canceled waiter or owner
exit alone does not clear ownership. Continuation reads the cached admission
seam and selects only heartbeat or fixed Automatic; it never invokes entry.

## Watch out
A failed channel stays fenced. The service confirmation deadline and native
lease are separate: late full proof on an intact channel can release the lease
without clearing service recovery. The fixtures establish no native authority.
