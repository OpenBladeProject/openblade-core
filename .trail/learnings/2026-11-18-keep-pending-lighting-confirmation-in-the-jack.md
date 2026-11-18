---
title: Keep pending lighting confirmation in the submitted scope
author: Jack
date: 2026-11-18
branch: fix/pr-977-lighting-review
touches:
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WriteConfirmationPolicy.cs
  - src/OpenBlade.Tray.Core/LightingEditorState.cs
  - tests/OpenBlade.Service.Tests/Rz090529IndependentZoneTests.cs
  - tests/OpenBlade.Service.Tests/WriteConfirmationPolicyTests.cs
  - tests/OpenBlade.Tray.Tests/LightingEditorStateTests.cs
---

## Decided
Project the confirmation target from the exact capabilities used for the write. A field-only write on a port offering no keyboard effects has field-only pending readback even if the saved profile opted into an effect. Keep saved profile data intact.

## Tried and ruled out
A temporarily empty effect list does not by itself retire prior explicit ownership. An explicitly field-only saved profile never authorizes a stale remembered effect during restart or drift recovery.

## Watch out
An unavailable standalone logo remains unconfirmed, and a known standalone, independent, or embedded logo supplies the same readback. Switching Off to a visible effect displays inherited positive brightness or replaces a saved zero with the visible default.
