---
title: Drain the scripted HID metadata lease before service STOPPED
author: Jack
date: 2026-11-10
branch: test/windows-rust-metadata-lifecycle
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/tests/service_fixture.rs
  - tools/openblade-windows-host/src/windows/scm/tests/metadata_lifecycle.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
Use a test-only driver over the existing Rust Windows service run_service and private v79 endpoint. Script discovery and handles through the existing HID lease owner. Only deviceVerified and revision change on the wire; writesBlocked stays true and no mutation scenario is attached. STOP waits for the same owner to retire the same handle before successful terminal publication.

## Tried and ruled out
The existing private-pipe smoke timed out under the low-integrity sandbox token and passed under a verified non-elevated medium-integrity token. The focused lifecycle test passed under that ordinary token as well. A same-path replacement does not revive an old epoch.

## Watch out
Compile the Win32 HID adapter separately; it is excluded from test builds. Keep native HID discovery, reports, hardware mutation admission, SCM, and the separate writer fence outside this fixture.
