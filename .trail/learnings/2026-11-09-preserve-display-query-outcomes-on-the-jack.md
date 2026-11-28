---
title: Preserve display query outcomes on the private Rust Session Agent pipe
author: Jack
date: 2026-11-09
branch: feat/rust-session-agent
touches:
  - ".github/workflows/rust.yml"
  - build/PublishPayloadValidation.ps1
  - docs/windows-backend-replay.md
  - src/OpenBlade.Tray/SessionDisplaySettingsAdapter.cs
  - tests/Build.Tests/PublishPayloadValidation.Tests.ps1
  - tests/OpenBlade.Session.Tests/WindowsReplaySessionTests.cs
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/pipe.rs
---

## Decided

Phase 6a uses a separate foreground executable in the existing standalone Windows tools workspace. Share the private pipe's token verification, first claim and listener replacement with the developer service host. Keep the display namespace, dispatch and lifetime separate. Version 2 query is the only admitted operation. The production client is source-linked with a constructor compiled only into the test assembly.

## Tried and ruled out

The v1 Session replay contained display requests but no display responses. Freeze both available and unavailable query snapshots and round-trip them with the managed codec. Keep the query fixtures synthetic and the new executable out of the installer payload.

## Watch out

The managed applier can complete a query successfully with IsAvailable=false. Preserve that distinction from transport or dispatch failure, and retain null unknown fields. The C# duplex client's generic-write access includes pipe-instance creation for the allowed account. The retained first claim rejects another first-instance host but does not create a boundary against processes acting as the same user. Cross-session requests still require fresh kernel session and token SID/session/logon checks. Drop each overlapped listener to discard prior peer bytes and retain the synchronous claim across replacement.
