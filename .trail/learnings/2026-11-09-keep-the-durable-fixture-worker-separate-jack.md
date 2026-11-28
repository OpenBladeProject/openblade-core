---
title: Keep the durable fixture worker separate from the Windows pipe
author: Jack
date: 2026-11-09
branch: feat/rust-durable-host
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/configuration_writer.rs
  - tools/openblade-windows-host/src/configuration_writer/fixture.rs
  - tools/openblade-windows-host/src/lib.rs
  - tools/openblade-windows-host/src/runtime.rs
  - tools/openblade-windows-host/src/runtime/tests.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
Create the developer child relative to the pinned Windows Temp directory and keep the exclusive fixture lock through host shutdown. Once accepted, the single blocking transaction is joined, and only verified readback enters the runtime reducer.
## Tried and ruled out
Write exclusion on the shared Temp handle can block another application's rename. Calling flushes on the current-thread pipe executor also blocks unrelated clients. Keep artifact-cleanup errors separate from the verified commit and its restart admission.
## Watch out
The harness deadline covers this disposable process. Production needs its own storage and ownership gates. If the initial root announcement fails, the creating process still owns teardown.
