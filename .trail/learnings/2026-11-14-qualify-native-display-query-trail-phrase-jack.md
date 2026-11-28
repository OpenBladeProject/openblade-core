---
title: Qualify native display query Trail phrase matches
author: Jack
date: 2026-11-14
branch: feat/native-agent-display-query
touches:
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/build.rs
  - tools/openblade-windows-host/src/installed_agent_main.rs
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/native_package/tests.rs
  - tools/openblade-windows-host/src/pipe.rs
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
---

## Decided
The exact accepted-base native display query Trail check exits 1 with 20 capped lexical conflicts. Keep the check JSON and per-finding semantic triage. Its matches do not establish a regression: this explicit partial Agent selection claims only its implemented display query and optional battery endpoint. It is not full sign-in replacement or deployment readiness, and it preserves first-instance ownership and fresh peer/origin checks.

The package producer and validators admit exact selected Agent capabilities without granting Service capabilities. The tests positively reject cross-role, incomplete, duplicated and full-display capability claims. Real CCD/WCS reads appear only in the selected production supplier; synthetic display data is test-only. WMI and mutations remain deferred. HDR Unknown withholds the snapshot and ICC default. Diagnostic origin/listener causes remain distinct from the neutral fallback. Read-only admission adds no display-on or thermal prerequisite.

## Tried and ruled out
Removing prior learnings or silencing the nonzero result cannot replace semantic review. Historical private replay and brightness fixtures are not runtime evidence for this stage. The blocking read future is retained and joined, not dropped; generic two-feature blocking tests prove supervisor drain only. No process tree, hardware, installed endpoint, or real supplier execution is claimed.

## Watch out
The checker phrase list is capped rather than exhaustive. Review the literal changed code, focused dispatcher and lifetime tests, actual production-decoder frames, package tests and four selected production compilations. The final commit records why the source is correct despite these advisory conflicts. Parent-owned immutable Claude review and acceptance remain separate.
