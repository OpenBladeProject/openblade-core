---
title: Preserve original native wire bytes in managed bridge evidence
author: Jack
date: 2026-11-12
branch: feat/installed-thermal-mutation
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tests/OpenBlade.Contracts.Tests/NativeThermalMutationResponseTests.cs
---

## Decided
Keep actual encoded response bytes as cross-language wire evidence. Emit the original UTF-8 payload read from the installed dispatcher before parsing can alter property order; use parsed values only for semantic assertions. The extraction packet must retain those raw payload strings. Keep the managed decoder strict and report all per-frame decode failures before failing the bridge.

## Tried and ruled out
The f3978070 Rust suite passed, but its marker printed serde_json::Value rather than the original payload. Sorted map serialization moved frame after archive/error/errorCode, causing the managed discriminator-first decoder to reject the derived evidence. Production StatusResponse declares frame first and serializes the typed struct directly. Reordering captured JSON or widening the decoder would hide this fixture defect instead of preserving actual wire bytes.

## Watch out
Retain the failed managed log and old transformed packet as intermediate evidence. Assert discriminator-first on the original bytes, preserve exact payload hashes through extraction, and do not filter partial Custom observations or fabricate settings. This correction changes test capture and diagnostics only; production framing and validation remain unchanged.
