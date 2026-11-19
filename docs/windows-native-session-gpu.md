# Native Session GPU endpoint

The installed Rust Session Agent can serve the Session GPU app list and its
close and force-close actions when it is built with `agent-gpu-v1`. The Tray
keeps using its unchanged managed client (`SessionGpuIpcClient`); the managed
Session Agent and its GPU endpoint are unchanged.

## What the build contains

- `openblade-platform-windows` feature `gpu-close`: the retained-handle port of
  managed `WindowsGpuAppManager.CloseGracefully` and `ForceClose`, and
  `close_windows_gpu_apps`, which takes a fresh scan, matches the requested
  targets against its complete projection and then acts. The `scripted-gpu`
  features never imply it.
- `tools/openblade-windows-host` feature `agent-gpu-v1` (implies `gpu-close`):
  the managed `SessionGpuPipeServer` over `SessionGpuRequestDispatcher`.
  - Pipe `OpenBlade.Session.Gpu.v2.<16 hex>`, the managed hash of
    `<user SID>|<session>`, two instances, and the managed one-SID protected ACL.
  - The managed v2 request and response format, 64 KiB frames, 2 s frame
    deadlines and a 20 s dispatch deadline.
  - The managed status and error strings, and the 128-app and 128-target caps.
  - `Readiness.gpu`.
- The GPU endpoint runs beside the Battery and Display endpoints under one
  feature lifetime. Stop signals every endpoint and waits for each to finish.
  Stop during a close leaves the remaining targets unattempted and writes no
  reply, as managed does.
- The native package epoch admits the `session-gpu-apps-v1` capability only for
  a build that selected `agent-gpu-v1`, in both the Rust package admission and
  `WindowsNativePackage`.

## Deliberate differences from managed

Endpoint (`tools/openblade-windows-host/src/session_native/gpu.rs`):

- Requests must use the encoding the managed codec writes: camelCase names,
  JSON numbers, no duplicate members and `operation` first. Managed also reads
  other name casing, numbers given as strings and duplicate members. Both
  accept upper-case GUIDs and extra whitespace. The echoed `requestId` is lower
  case, as managed writes it.
- A client disconnect or an extra byte during dispatch cancels the operation
  in the same way as Stop or the deadline. Managed ignores the client until
  the dispatch ends.
- When the app list does not fit in a 64 KiB frame, the reply keeps the
  largest ordered prefix that fits, and a query then reports partial success.
  Managed fails the write and closes the connection. Managed escapes non-ASCII
  names, so very long localized names reach this limit first.
- After a close reports an unconfirmed submission, or is stopped after one,
  every later close is refused with "A previous close could not be confirmed.
  Refresh the GPU app list first." until a query succeeds. This fence covers
  every connection to the endpoint.
- A lost query worker replies with the managed dispatch-failure text. A lost
  close worker ends the endpoint without a reply, because a close may already
  have been submitted.
- Scan failures map as follows:
  - A current-user or name-comparison failure replies with the dispatch-failure
    text.
  - Cancellation writes no reply.
  - Counter, adapter and data failures reply with "Windows GPU activity
    counters are unavailable."

Close backend (`rust/crates/openblade-platform-windows/src/gpu_apps/action/native.rs`):

- Process handles ask only for query, synchronize and, for force close,
  terminate access. Managed asks for `PROCESS_ALL_ACCESS`.
- The process's identity, owner and user are read again on the same handle
  that is acted on: PID, creation time, user SID, session, image, exit state
  and the parent the scan would group it under. A reused PID, an exited
  process, a UNC or device image path, or a changed owner reports the row as
  changed.
- A failed `WM_CLOSE` post is a failure. Managed reports it as success.
- A force close succeeds only when process exit is observed within 5 s.
  Otherwise it is unconfirmed. Managed reports success once
  `TerminateProcess` returns.
- Force close never falls back from the tree to the exact process.
  Descendants are terminated only after the root's exit is confirmed. Each
  descendant must belong to the current user and session and must not have a
  protected name. The walk visits at most 256 descendants and ends on Stop.
- A failed host name comparison refuses the submission.

## Offline verification

These checks use scripted suppliers, scripted process tables and in-memory
pipes. None samples GPU counters or opens, signals or closes a real process.

- Platform crate: `cargo test --locked -p openblade-platform-windows` with
  `gpu-close`, `scripted-gpu-close` and both together. The backend tests cover:
  - graceful close, and a force-close tree that skips a reused PID, a foreign
    account and a protected name
  - unconfirmed or interrupted exit reported as unconfirmed, and refused
    termination
  - the Agent in the tree, and changed or unreadable identity
  - a same-name parent owner and a failed name comparison
  - a missing window and a failed post
  - partial success, and Stop between targets and during the tree walk
  - every opened handle released
- Host: `cargo test --locked --manifest-path tools/openblade-windows-host/Cargo.toml
  --bin openblade-windows-agent-candidate --features agent-gpu-v1`, alone and
  with Display and Battery. The endpoint tests cover:
  - query projection, truncation and unavailable mapping
  - close ordering, and partial, failed and successful closes
  - the fence across connections
  - invalid frames refused before any supplier call
  - lost workers
  - Stop, deadline, disconnect, extra byte and origin loss, each writing no
    reply and joining the worker
  - frame trimming, and the pipe name hash
- Managed parity oracle (`tools/openblade-windows-host/tests/oracles/gpu_apps`)
  runs the production managed codec and dispatcher:
  - `managed-requests.jsonl` holds 10 managed-written requests and 55 that the
    managed reader rejects. Native accepts exactly the first set.
  - `managed-dispatch.jsonl` holds 13 dispatcher responses over scripted scans
    and close results. The native mapping writes the same responses.
  - `responses-verify` decodes every native frame, including the native-only
    replies and a trimmed list, with the managed reader.
- The Deployment epoch tests check that only a build that selected
  `agent-gpu-v1` can claim `session-gpu-apps-v1`.

## Runtime validation still needed

None of the following has been run:

- the production sampler and the close backend against live PDH counters,
  DXGI adapters, the process table, windows and termination
- the endpoint on a real named pipe, including its ACL, the peer identity check
  and two instances
- an installed or packaged native Agent, and the Tray against it on a live pipe
- force-close timing on a real process tree, including the 5 s exit
  confirmation and the 20 s dispatch deadline
- localized and long process names on a real system

Physical or attended validation needs separate authorization. It should use one
expendable current-user test process. Query, close it, then confirm by a fresh
query that it has gone.
