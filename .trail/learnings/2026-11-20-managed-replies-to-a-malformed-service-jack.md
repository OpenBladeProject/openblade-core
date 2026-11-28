---
title: Managed replies to a malformed Service request before closing
author: Jack
date: 2026-11-20
branch: feat/native-service-refusal-and-profiles
touches:
  - docs/windows-rust-development-service.md
  - src/OpenBlade.Service/NamedPipeControlServer.cs
  - tools/openblade-windows-host/src/service_status/unserved.rs
---

## Decided
- This corrects the 2026-11-20 learning "Native typed refusal for unserved Service operations", which said managed's codec would close on an invalid body. When `ReadRequestAsync` throws `InvalidDataException`, `JsonException` or `IpcProtocolVersionMismatchException`, `NamedPipeControlServer` writes `Failure(Guid.Empty, "IPC request is malformed.")` and then returns (first request and later requests alike). `Failure` replaces an empty id with `Guid.NewGuid()`, so the reply is uncorrelated.
- So a malformed body for an operation native does not serve gets the correlated unsupported refusal from native, where managed sends the uncorrelated malformed-request failure. Native's own unknown, malformed or wrong-version frames still close without a reply; that is existing native behaviour, not managed parity.

## Tried and ruled out
- Treating "managed closes on a bad body" as parity evidence: managed answers first.

## Watch out
- The publication guard on the refusal path runs before and after the write. `refusal_is_published_only_while_the_status_origin_holds` pins both calls.
- The compiled `serves` table is pinned only by `served_operations_are_exactly_those_the_typed_decoder_admits` running in every Service lane. The fan and thermal-only rows need their own CI runs.
