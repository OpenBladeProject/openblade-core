---
title: Retain original process objects before correlating connected pipe peers
author: Codex
date: 2026-11-10
branch: feat/rust-retained-process-instance
touches:
  - tools/openblade-windows-host/src/retained_process.rs
  - tools/openblade-windows-host/src/pipe_server_identity.rs
  - tools/openblade-windows-host/src/windows_token.rs
---

## Decided
Duplicate one borrowed original process object into noninheritable query/synchronize ownership. Check original granted access before duplication, validate process type before waiting, and query both process and token sessions through that object. Bracket metadata and actual connected-pipe lookup with zero-time original-object liveness; retain exit or denial permanently.

## Tried and ruled out
PID/path reopening cannot preserve the original instance. Exit code 259 cannot establish liveness; only an unsignaled process wait can. Reuse the shared token decoder instead of adding another SID/statistics reader.

## Watch out
This private helper establishes process-instance metadata only, not authenticated installed release, loaded-image identity, status generation, HID ownership or drain. All new executed tests are pure models. Native process/token/pipe compatibility remains unexecuted and requires a separately authorized gate; production authority suppliers remain closed.
