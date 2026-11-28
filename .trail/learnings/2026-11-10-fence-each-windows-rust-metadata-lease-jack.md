---
title: Fence each Windows Rust metadata lease generation
author: Jack
date: 2026-11-10
branch: feat/windows-rust-hid-service-owner
touches:
  - docs/windows-backend-replay.md
  - tools/openblade-windows-host/src/discovery/hid_lease.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/owner.rs
---

## Decided

The private Windows Rust metadata owner serializes acquisition and retirement in one service driver. It binds passive checks and invalidations to an opaque acquisition epoch. Every acquired handle, including an open rejected by the second scan or descriptor read, enters the same retirement fence before replacement.

## Tried and ruled out

A handle drop alone cannot confirm callback drain. The managed provider can retain a cached object for passive status, but that fallback cannot establish a fresh opened-handle lease or admit writes.

## Watch out

Keep the owner alive through the service driver drain. Pending retirement holds the original handle and uncertainty blocks replacement. The compile-only native adapter still needs an explicit retirement implementation and attended open, removal, callback and reconnect validation before integration.
