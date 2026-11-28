---
title: Fence private display submissions across fixture restarts
author: Jack
date: 2026-11-10
branch: fix/rust-windows-phase-6v
touches:
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_display/journal.rs
  - tools/openblade-windows-host/src/session_windows.rs
---

## Decided
- Keep the private v2 display fixture fence in a bounded versioned record keyed to the fixture ID, logon and session. The installed display automation checkpoint tracks profile ownership and paused fields. It cannot identify a specific private worker, panel or attempt.
- Publish intent before the scripted effect and retire only the same intent after independent readback and response delivery. The replacement process must claim the original endpoint after its old owner exits. Authorize reconciliation of a pending startup record explicitly.

## Tried and ruled out
- A matching observation inside the original process is insufficient while the old worker can still act. Restarting in ordinary resume mode leaves the intent pending. Failed replacement, missing or unreadable records, wrong panels and third values leave the fence in place.

## Watch out
- The replacement fixture supplies synthetic observations from its own read side. A baseline or requested value allows reconciliation of that fixture only and does not establish persistence of a real display effect or admission for native writes.
