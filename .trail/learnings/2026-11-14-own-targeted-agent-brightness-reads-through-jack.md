---
title: Own targeted Agent brightness reads through native cleanup
author: Jack
date: 2026-11-14
branch: feat/native-agent-owned-brightness
touches:
  - rust/crates/openblade-platform-windows/src/brightness.rs
  - rust/crates/openblade-platform-windows/src/brightness/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/native.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/native/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker/tests.rs
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
---

## Decided
The selected native Agent display query uses the real local WMI transport with a distinct BrightnessQueryTarget and managed-shaped InstanceName/CurrentBrightness/Level projection. Exactly one valid matching Active row supplies percent and sorted unique levels 0..100. Invalid/missing/ambiguous WMI data leaves brightness absent while CCD/HDR/ICC remain usable; detected target replacement discards the combined snapshot. Setter admission remains separate and unchanged.
The installed Agent initializes its process COM security once before features start and retains the entry-thread MTA guard until monitoring and both selected features drain. A failed or unknown prior policy is cached and disables only WMI brightness. Worker shutdown closes admission and joins its actual thread, including COM proxy/apartment destruction; the host performs this from its owned blocking supplier before reporting completion.
## Tried and ruled out
A reply timeout or Completion flag does not prove COM/thread retirement. Do not detach or replace a pending reader. No hard Stop deadline or isolated process is introduced: synchronous provider work and cleanup may take longer than the reply budget. Query-only read semantics do not require the setter-only __CLASS/Levels-count/ASCII checks. Read matching uses Windows ordinal comparison and signed Int32 suffixes; bounds remain 64 rows, 512 UTF-16 instance/path units and 256 raw level entries. Culture-specific nonstandard sign strings conservatively do not match.
## Watch out
Injected tests hold native work and destructor separately and run the actual worker, supplier shutdown, dispatcher cancellation and composed feature drain. Actual dispatcher frames pass the production C# codec including all101 levels, failures and target replacement. These software fixtures are not real WMI, installed-role, display, COM-provider timing or hardware proof. The default managed app, mutations, automation and full native cutover remain outside this stage. Root owns frozen tools-disabled Claude review and acceptance; no provider call or next stage starts here.
