---
title: Hold the Rust installed pipe claim through readiness and retirement
author: Jack
date: 2026-11-10
branch: feat/rust-protected-readiness-pipe
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_windows.rs
  - tools/openblade-windows-host/src/pipe_primitives.rs
---

## Decided
Use the managed protected ACL with exact numeric rights and reject remote pipe clients. Keep the exclusive first instance parked on a verified local peer, and give each status exchange its own handle. Resolve the request token only after reading the frame.

## Tried and ruled out
Do not reuse the development routing or its ACL. The installed provider stays unavailable until independently admitted; the injected and private fixtures never supply its status. The private normal-token transport test uses an owner-only ACL because the managed Users ACE cannot create an additional server instance.

## Watch out
A peer reaching the first instance before the self-claim can stop startup safely. Session authority failure must stop the candidate; keep request-token failures local to their exchange. Do not call FlushFileBuffers against an untrusted client. Trail's lexical matches to fixture sources, unrelated write authority and stored hardware state do not apply: this change routes only GetStatus and changes no hardware behavior.
