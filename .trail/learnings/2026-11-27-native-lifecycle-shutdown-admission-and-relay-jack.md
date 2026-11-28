---
title: Native lifecycle shutdown admission and relay timing
author: Jack
date: 2026-11-27
branch: feat/native-lifecycle-ops
---

## Decided
- Native shutdown admission is the pure seam `system_or_administrator(system, membership)` in readiness_peer.rs; every unreadable answer is no, and membership is asked only of a readable non-SYSTEM user. The pipe answers membership with CheckTokenMembership on the impersonated client token (BUILTIN\Administrators), matching managed IsInRole; Stream has no defaults for it.
- After the operation gate frees, serve_shutdown re-probes the pipe (PeekNamedPipe `peer_closed`) before admission: NativeStream reads retry every 20 ms, so a read poll can miss a disconnect.
- The relay ages a report from the connection's receipt (`requested` in serve_connection) and, at an equal stamp, treats the newest transition as clear if either the SCM gate or its own last accepted report is clear; that is managed's one-gate state in both arrival orders (vectors tie/*).
## Tried and ruled out
- Aging with `received.elapsed()` against a QPC test clock that stands still: every exchange test expired. Relay exchange tests run with `start_paused`, and the mutation Rig's clock advances with tokio time.
## Watch out
- TaskStop on a bash loop that launches cargo via powershell leaves the loop and cargo running; find them by the scratchpad cargo.ps1 path in their command line before stopping, never by name alone.
- The admission token tests assume a non-elevated test process; elevated, the membership-ignored mutant survives.
