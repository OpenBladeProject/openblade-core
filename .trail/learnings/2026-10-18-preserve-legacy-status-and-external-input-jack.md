---
title: Preserve legacy status and external input during Blade 18 rollout
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - src/OpenBlade.SessionAgent/Blade18DefaultFnInputState.cs
---

## Decided
Preserve the frozen protocol-51 read-only wire shape when adding Blade 18 capabilities. Omit blade18KeyboardLayoutId even when null, remove unknown layout/Sequence capabilities and confirmed data, project required desired/profile lighting to a valid read-only placeholder, and preserve compatible RZ09-0581/RZ09-0528 sequences. Apply this in the shared codec because an unknown null member breaks legacy status on every model.

## Tried and ruled out
Cancelling a foreign Raw Input action after the low-level hook already swallowed its key does not preserve external input. The Blade 18 Fn backend now checks fresh, bounded keyboard topology and a confirmed local session before suppression and confirmation. Unknown, foreign or remote input passes through and cancels pending actions/latches. RDP keyboards are absent from Raw Input enumeration, so device enumeration alone is insufficient. Explicit global Gaming Mode remains separate and unchanged.

## Watch out
Do not claim per-device suppression from this guard: it admits interception only while all enumerated keyboard collections identify as the Blade 18 in a local session. Keep the owner's Fn check to the built-in keyboard. Query failures must preserve input and report a reason, never replay swallowed keys. Both fixes require final combined-head tests; earlier 03281769 tests and 164540b private-installer results do not certify the new head. No hardware writes, live input source or injection are part of these offline regressions.
