---
title: Classify lexical review warnings on dormant acquisition
author: Jack
date: 2026-11-10
branch: feat/windows-rust-battery-acquisition
touches:
  - tools/openblade-windows-host/src/session_battery/acquisition.rs
  - tests/OpenBlade.SessionAgent.Tests/WindowsBatteryReportParityTests.cs
---

## Decided
The review gate's warnings on this slice are lexical overlaps. The fixture proves the ordering regression and both implementations now choose the same precedence. The dormant helper owns one child and retires it before cleanup.

## Tried and ruled out
Changing the fixture to silence keyword matches would lose the regression evidence. The other matches name separate subsystems.

## Watch out
Review the reported lines and their referenced contracts before accepting an exception to the gate. Record that disposition in the local commit message.
