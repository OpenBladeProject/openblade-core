---
title: Bind the durable Windows replay to a fresh fixture root
author: Jack
date: 2026-11-09
branch: feat/rust-durable-host
touches:
  - docs/windows-configuration-recovery.md
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/configuration_writer.rs
  - tools/openblade-windows-host/src/lib.rs
  - tools/openblade-windows-host/src/runtime.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
Phase 4e composes the exact managed aggregate fixtures, Windows writer and restart inspector with the existing runtime and passive private pipe. Only the process that creates a randomly named fixture can write. Restart requires a pinned root identity, uses a read-only lock and refuses any sidecar before creating the endpoint.
## Tried and ruled out
Keeping the whole directory chain open without write sharing breaks same-directory MoveFileExW and causes native sharing failures. A brief write-exclusive leaf pin protects creation. The held lock file keeps the fixture nonempty while the regular directory handles deny deletion. The managed test retains its own directory pin across process exit and restart.
## Watch out
The reported limit is requested configuration and the getter is synthetic. Interrupted replacement and failed rollback preserve the record, preimages and remaining stages. Local fixture custody and injected faults cannot establish production ACL, namespace, ownership or storage guarantees.
