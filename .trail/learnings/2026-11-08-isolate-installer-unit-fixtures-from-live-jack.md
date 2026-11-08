---
title: Isolate installer unit fixtures from live guardian records
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-cpu-temperature-source.md
  - docs/so6120-stop-auto-guardian.md
  - tests/OpenBlade.Setup.Tests/SetupGuardianQuiescenceGuardTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformTestFactory.cs
---

## Decided
WindowsSetupPlatformTestFactory must supply an inert guardian-quiescence test double by default, just as it supplies other controlled setup dependencies. Tests that verify guardian protection explicitly inject the production guard over temporary paths and fake process observations. All 427 Setup tests pass with the installed guardian evidence still protected.

## Tried and ruled out
After a real product Stop refusal created the protected FanStopGuardian directory, four mocked service-order tests failed with AccessDenied. The factory had left GuardianQuiescenceGuard null, which silently selected production ProgramData and process enumeration. This was fixture leakage, not a broken production admission check. Do not elevate unit tests, delete machine records or weaken ACLs to make them pass.

## Watch out
Retain explicit real-guard coverage for pending evidence, inaccessible records, helper ownership and binary/data removal. A fake default is for the test factory only; production continues to fail closed when quiescence cannot be established. This tests-only correction does not require repackaging an unchanged product binary.
