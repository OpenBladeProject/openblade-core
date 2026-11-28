---
title: Keep partial display replies available while WMI retires
author: Jack
date: 2026-11-14
branch: feat/native-agent-owned-brightness
touches:
  - tools/openblade-windows-host/src/session_native/display_query.rs
  - tools/openblade-windows-host/src/session_native/display_query/observation.rs
  - tools/openblade-windows-host/src/session_native/display_query/observation_tests.rs
  - tools/openblade-windows-host/src/session_native/display_query/tests.rs
  - tools/openblade-windows-host/src/session_native/display_query/brightness_lifetime_tests.rs
---

## Decided
A timed-out native WMI brightness reply must leave independently available display fields usable, including on later queries while the old native request or its local COM cleanup remains held. The display feature now owns one WindowsBrightnessWorker across requests. Its existing pending flag yields brightness-unavailable on subsequent requests without queuing or spawning replacement native work; each admitted request still binds a fresh CCD query target. Request-local COM temporaries finish before request Completion, while the cached connection/thread intentionally remain feature-owned.
Every connection-loop exit unconditionally awaits supplier drain on a blocking thread. Drain closes admission and joins the actual thread/COM owner before endpoint/Origin and composed feature custody are released. A disconnected existing worker is fatal and joined rather than hidden as permanent partial success or replaced.
## Tried and ruled out
The4c5678c3 implementation joined worker shutdown before projecting a partial snapshot. Root validated that a held native call/cleanup could consume the outer dispatch window and close the response without a snapshot. This supersedes only that earlier learning's per-request join placement, retaining its positive cleanup/Stop requirements. Sending one partial response and then blocking all later full queries on cleanup is also insufficient. Increasing the outer timeout, detaching a worker or inventing finite hardStop guarantees does not resolve this availability contract.
## Watch out
Focused fixtures hold the native call, request-local destructor and owner destructor separately, exercise repeated partial native frames and recovery without overlap, and drive Stop/Origin failure through the composed feature supervisor. Five fixture-only managed oracle invocations decode and round-trip actual frames while native gates are still held. Ordinary failures retain partial fields; worker panics stop and join the feature. Default managed behavior, mutation admission, IPC, transport and model scope remain unchanged. Real WMI timing and installed-role/hardware behavior are unexecuted. Original4c sealed evidence remains immutable; Root owns the correction's frozen Claude review and acceptance.
