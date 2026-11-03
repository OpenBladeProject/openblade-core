---
title: Close interrupted user context and native ownership lifetimes
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - src/OpenBlade.SessionAgent/SessionDisplayRequestDispatcher.cs
  - src/OpenBlade.Tray.Core/DisplaySettingsViewModel.cs
  - src/OpenBlade.Contracts/IpcJsonValidation.cs
---

## Decided
Bind a user's SID, authority predicate and stores coherently during activation, and keep an incomplete marker from before old-context teardown until finalization. Authenticate and record a queued user admission before waiting for request serialization; canceled waits release their pending count but must not restore an old epoch. Preserve fresh write admission and all uncertain-submission trackers. Complete admitted display operations in finally even when requested cancellation escapes, using an indeterminate result until independent recovery succeeds. Retain current and retiring keyboard hook handles until confirmed removal, stop on failed retirement, and pass callbacks through after terminal failure. Capture display edit baselines after the operation gate.

## Tried and ruled out
Putting every initial activation check behind the operation gate delayed session-change invalidation and failed existing recovery tests. Setting the incomplete marker only at the store swap missed interruption during old lighting teardown. Releasing the pending admission before marking teardown incomplete left an ABA interval. A false UnhookWindowsHookEx result is not documented proof that a timed-out hook is absent, so do not convert unknown cleanup into success. Keep the complete duplicate-member prevalidation contract ahead of version checks and custom converters; replacing it with the serializer duplicate-property flag is not established equivalent.

## Watch out
Low-power ingress must bypass ordinary request serialization. Legacy protocol-51 cached status must remain responsive. Current-user status paths may require activation; never gain responsiveness by returning another user's not-yet-activated context. Final cleanup must retain held-key uncertainty, and Windows can deliver sent hook messages before queued WM_QUIT. Tests use fake adapters/native failures, not live hooks or hardware. The production-codec benchmark showed 15-17 percent lower median decode time for two synthetic frames after streaming prevalidation; allocations were essentially unchanged and this is not an app-wide latency or power measurement. Keep persisted JSON's Ordinal member policy distinct from IPC's OrdinalIgnoreCase policy.
