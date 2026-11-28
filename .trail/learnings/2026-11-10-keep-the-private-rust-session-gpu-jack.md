---
title: Keep the private Rust Session GPU endpoint query only
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6o-session-gpu-query
touches:
  - tools/openblade-windows-host/src/session_gpu.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling/scripted.rs
---

## Decided
Use the private same-user GPU pipe for a single managed v2 request. Query feeds fixed observations through the Phase 6n sampling and projection path. Keep inventory eligibility inside the projection; private wire rows cannot close and close requests return a typed failure before sampling.

## Tried and ruled out
Keep inherited Session Agent and GPU sampler histories in a two-parent checkpoint. The checkpoint Trail matches are lexical matches in files unchanged from the Session parent; only the checkpoint-to-integration diff describes Phase 6o.

## Watch out
A 128-row inventory can exceed the byte cap with valid Unicode names. Keep the ordered prefix, distinguish partial replies from no data and available-empty results, and drain cancelled scripted scans before accepting another request. Live process parent evidence and actions remain separate gates.
