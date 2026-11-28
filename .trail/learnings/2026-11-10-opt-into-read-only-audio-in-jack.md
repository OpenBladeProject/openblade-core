---
title: Opt into read-only Audio in the unified Windows fixture
author: Jack
date: 2026-11-10
touches:
  - tools/openblade-windows-host/src/session_windows/integrated.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Integrated.cs
  - rust/README.md
---

## Decided
Keep the exact Battery/SCM and Audio candidate parents. In the unified fixture, audio-available adds the fixed synthetic read-only scenario. Claim and open its pipe with the other requested endpoints before READY. Keep simulated spatial writes in the explicit standalone Audio scenarios. Ordinary scratch Setup requests neither optional endpoint.

## Tried and ruled out
Adding Audio to ordinary readiness would change the signed rehearsal contract. The unified endpoint uses the read-only fixture rather than selecting a mutable scenario. The Battery/SCM-relative Trail check matched wording brought in from the Audio parent's README, including unrelated native and signer terms. The merge adds neither a native Audio backend nor a new signature check.

## Watch out
The managed Audio v2 cases and signed scratch rehearsal exercise local scripted peers. Native endpoint evidence, real device action, service registration and installed recovery require separate admission and validation.
