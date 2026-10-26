---
title: Retain complete redacted runtime failures across shipped boundaries
author: Jack
date: 2026-10-24
branch: fix/runtime-diagnostic-coverage
touches:
  - src/OpenBlade.Contracts/DiagnosticFailure.cs
  - src/OpenBlade.Contracts/DiagnosticTrace.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Platform/WindowsRazerDriverApiDiscovery.cs
  - src/OpenBlade.Service/DiagnosticHistory.cs
  - src/OpenBlade.Tray.Core/PersistentTraceLog.cs
---

## Decided
The maintainer explicitly requested comprehensive runtime diagnostics after Nightly 101 still reduced an admission InvalidDataException to UnexpectedFailure. Preserve redacted actionable messages, thrown stack locations, native codes, complete ordinary inner/aggregate trees, package/discovery context and controller-observation failures. This supersedes the earlier choice to exclude all exception messages and retain only four causes. Finite record/retention limits remain, with explicit truncation and export omission metadata. Keyboard contents, credentials, raw exception Data and diagnostic authority remain excluded.

## Tried and ruled out
Catching only another exception type or adding another enum would require another owner report without identifying the validation branch. InvalidDataException derives directly from SystemException, so IOException catches do not contain it. Shared package discovery must contain structural rejections and preserve the actual package/check; compatible driver updates remain admitted through the API contract, without binary-version allowlists.

## Watch out
Rich records are useless if a downstream sink drops them. Service ILogger previously discarded Exception; service history retained only 12 KiB; diagnostic IPC allowed only a 32 KiB ZIP. Protocol 72 adds a dedicated bounded diagnostic response, leaving ordinary requests/status/subscriptions at 64 KiB and retaining the protocol-51 read-only projection. Use a coordinated product update. User-mode Trace sinks, service/broker/setup bridges, rotation, concurrent writers, status-file limits and ZIP inventory are part of the same delivery path. Combined cleanup/original records must be rebudgeted with explicit truncation. Native return-code failures need observations even when no exception is thrown. These changes do not establish the owner's underlying package mismatch or physical SnapTap recovery.
