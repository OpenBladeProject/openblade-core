---
title: prove keyboard backend cleanup before handoff
author: Codex
date: 2026-10-30
branch: feat/keyboard-input-backends
touches:
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/ServiceReactiveLightingLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - src/OpenBlade.SessionAgent/SnapTapInputHost.cs
  - src/OpenBlade.SessionAgent/SnapTapProcessCoordinator.cs
  - src/OpenBlade.SessionAgent/WindowsFunctionRowOwnershipGuard.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - tests/OpenBlade.SessionAgent.Tests/FunctionRowDriverTests.cs
  - tests/OpenBlade.SessionAgent.Tests/KeyboardDriverPreferenceSourceTests.cs
  - tests/OpenBlade.SessionAgent.Tests/ServiceReactiveLightingLoop.BackendTests.cs
---

## Decided

Keyboard backend handoff needs separate proofs for a clean transition, a no-write admission rejection, and uncertain output. A helper that rejects obsolete or disabled launch preferences before native input returns code 3, never successful code 0. Keep real unexpected successful exits unsafe.

Use DriverOwnershipJournalReader for supervisor, Windows SnapTap, and Windows function-row admission. Read bounded 256-by-292-byte hook records and the exact filename/feature scope; a Windows path may accept only a valid strictly earlier UTC boot, without deleting the journal or opening the driver. Same-boot and unreadable evidence remain closed.

Publish Windows function-row status immediately from explicit preference and settled ownership notifications, outside key callbacks. Normal draining is WaitingForRelease; a sticky barrier or inactive unresolved journal is RecoveryRequired. Deduplicate status by payload and feature scope excluding UpdatedAt, and stop a retired producer from writing idle status over the selected backend.

The native Windows source and factory must explicitly prove transfer safety. Register ownership before native thread startup; only completed successful unhook with no abandoned suppressed key confirms cleanup. Retain uncertainty across constructor failure, timed-out disposal, source replacement and completion faults. An active idle source must be the only outstanding registration; a cleaned source needs no outstanding registration. Refuse a new hook while old ownership remains.

## Tried and ruled out

Claude's proposed same-boot journal deletion was unreachable: the reviewed supervisor already rejected equal boot identities. Re-reading on explicit selection and confirmed clean completion still recovers transient observation failures. Preserving a hypothetical future opaque backend token on every unrelated save is outside the current enum contract: unknown choices remain paused without selecting another owner.

A source's missing pointer or completed task does not prove cleanup. An isolated active replacement also cannot erase a previous failed unhook. Focused lifecycle tests exercise the shared production transfer predicate rather than launching real hooks.

## Watch out

Publish terminal status before clearing child ownership, then notify consumers after barriers are settled. Keep launch feature masks immutable across gate, ownership, arguments and terminal publication. Idle publication follows current selected roles without changing that launched mask. No-write exit codes must not weaken uncertain-output recovery.

Software tests and independent reviews do not establish real game suppression, physical Fn-row driver behavior, or installation success. Do not perform input injection, native hook launches, driver installation or hardware writes as part of these simulated regressions.
