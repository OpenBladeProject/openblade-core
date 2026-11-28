---
title: Native dev host follows IPC80; reporter pipe must move with it
author: Jack
date: 2026-11-19
branch: fix/native-branch-red-tests
touches:
  - src/OpenBlade.Tray/OpenBlade.Tray.csproj
---

## Decided
- The read-only development host (`src/wire.rs` `CURRENT_VERSION`) follows the product protocol; it is not a frozen v79 surface. It speaks IPC80 and embeds the managed v80 replay fixtures. The `tests/data/ipc79` copies are now rejection controls: `rejects_frozen_ipc79_status_reads_without_reply` checks that every v79 status read closes without a reply. This replaces the "dev host stays v79" decision in the earlier red-test learning from the same day.
- IPC80 needed no shape change for the dev host. The managed wire-shape manifest hash is the same for 79 and 80, and only the RZ09-0530 layout string was admitted.

## Tried and ruled out
- Pinning the Tray `RustReadOnlyHostTests` to 79. They use the managed `ServiceClient` and `IpcMessageCodec`, which only speak `OpenBladeIpc.ProtocolVersion`, so a v79 pin is not possible without a second managed codec.

## Watch out
- `src/reporter_pipe.rs` `VERSION` is the dev host's private reporter client. It must move in lockstep with `wire.rs` `CURRENT_VERSION`, along with the literals in `src/windows/reporter_tests.rs`. Otherwise `reporter_drives_unified_application_replay_over_private_*_pipe` fails with UnexpectedEof.
- The CI `Rust*Parity` Service and Protocol filters are managed-only and never start the dev host. Only the Tray `RustReadOnlyHostTests` (and `RustSessionAgentTests`, outside CI) use `OPENBLADE_RUST_DEV_HOST_EXE`. They are fixture-only: temp-root fixtures, a private pipe, and no SCM, device or driver. The one protected case skips unless `OPENBLADE_RUST_PROTECTED_FIXTURE=1`.
- Building Tray.Tests in a fresh worktree needs `dotnet restore src/OpenBlade.Tray/OpenBlade.Tray.csproj` first, as CI does. The test-project restore alone leaves Tray assets missing.
