---
title: Retire shared native pipe writes before releasing connection custody
author: Jack
date: 2026-11-14
branch: feat/native-agent-display-query
touches:
  - tools/openblade-windows-host/src/pipe.rs
  - tools/openblade-windows-host/src/session_audio.rs
  - tools/openblade-windows-host/src/session_battery.rs
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_gpu.rs
  - tools/openblade-windows-host/src/session_native/battery/tests.rs
  - tools/openblade-windows-host/src/session_native/display_query/tests.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/pipe/response.rs
---

## Decided
Every shared Endpoint listener now returns the bounded Connection adapter. Its AsyncWrite submits an owned overlapped WriteFile and reports bytes only after GetOverlappedResult confirms terminal completion. One pending write owns a duplicate submitting handle, stable boxed OVERLAPPED and frame storage, plus a private manual-reset event. The per-operation hEvent low bit prevents its foreign OVERLAPPED from reaching mio's IOCP handler. Tokio still owns read/connect, and no global handle notification flags change.

The largest admitted individual write is Battery's 256 KiB body plus its four-byte prefix. Display, GPU, Audio and service frames have smaller 64 KiB body bounds. All shared private Session and development-service consumers use the same adapter, so a stalled status stream cannot accumulate an unbounded queue of acknowledged-but-pending writes. Independent CPU-worker pipes and the separately implemented NativeEndpoint remain outside this shared transport change.

On write cancellation or deadline the caller discards the connection. Its pending write owner requests only targeted CancelIoEx and retains all storage until completion is confirmed. Connection field order retires the write before dropping Tokio's pipe. Drop may synchronously hold the current-thread runtime until Windows completes retirement; the deadline bounds the cancel request rather than promising finite overall Stop. Normal successful replies are never explicitly disconnected or flushed through a peer-consumption wait.

## Tried and ruled out
Tokio/mio write_all buffer acceptance and AsyncWrite flush/shutdown are not proof of kernel-write retirement. A larger output buffer does not establish a general lifetime boundary, especially for a multi-frame status subscriber. Do not ignore listener failures, add wire acknowledgements, shrink valid replies, replay a partial write, or deliver foreign OVERLAPPED packets to mio. The previously refuted ERROR_NO_DATA connect allegation, dual-fault diagnostic priority and hypothetical warnings policy were not changed.

## Watch out
The actual dispatcher produces valid 16,384-byte and maximum 65,540-byte Display frames from bounded profile data; the production C# decoder accepts them. Private random OpenBlade.Test.GUID pipes exercise unread-peer cancellation, a subsequent listener and complete normal delivery, plus explicit pending-future cancellation and fresh first-instance cleanup. An owned watchdog closes unread peers within eight seconds on another thread, then joins it. This is local software I/O evidence, not an installed endpoint, role, display supplier, powercfg or hardware observation. Parent owns frozen Claude review and acceptance; prior seals remain immutable.
