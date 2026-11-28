---
title: Compose private HID metadata fixture with Windows lifecycle evidence
author: Jack
date: 2026-11-10
branch: feat/rust-lifecycle-hid-fixture
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/tests/service_fixture.rs
  - tools/openblade-windows-host/src/pipe.rs
  - tools/openblade-windows-host/src/windows/scm/lifecycle.rs
  - tools/openblade-windows-host/src/windows/scm/tests/metadata_lifecycle.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
Compose the Windows-only private metadata test fixture with the existing bounded lifecycle worker and the service driver's STOP signal. Keep the pipe token and process-session checks. Only use freshly observed console and known power for passive verification. A callback immediately fences status, and the worker checks pending work again before publishing an inert acquisition.

## Tried and ruled out
Do not use session-notification IDs or the previous console to verify a lease. A queued callback can supersede even a completed metadata open; retire that epoch without publishing verified status. STOP discards queued callbacks and waits for the same owner before terminal status. No additional process or native HID path is required.

## Watch out
The Windows tests exercise simulated power and session evidence and a private v79 pipe. They do not admit native SCM registration, callback context teardown, live sleep or session changes, or native HID handles and reports. Trail check can match generic power, session, and retirement words from unrelated thermal, fan and Setup learnings; verify the cited behaviors and document any override in the commit.
