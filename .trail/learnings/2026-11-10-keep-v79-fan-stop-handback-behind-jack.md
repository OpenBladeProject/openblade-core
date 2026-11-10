---
title: Keep v79 Fan Stop handback behind scripted owner proof
author: Jack
date: 2026-11-10
branch: feat/rust-v79-guardian-fixture
touches:
  - tools/openblade-windows-host/src/fan_stop_fixture.rs
  - tools/openblade-windows-host/src/wire.rs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.FanStop.cs
---

## Decided
Use the existing v79 typed request on a dedicated Windows development endpoint. The manual entry replay supplies admission, and Phase 5q consumes scripted owner evidence. Keep ordinary fan mutation gated on handback and its own full-adapter fixture.

## Tried and ruled out
Automatic readback and the owner receipt were separate observations; neither alone released the delegation. A repeated live owner revision, missing cleanup fact or ambiguous signal retained recovery. A proven never-submitted refusal returned Inactive after exit.

## Watch out
The single operation belongs to the scripted owner after dispatch. Caller cancellation does not establish handback. The tests prove private transport and fixture behavior only; they do not exercise native ownership or device state.
