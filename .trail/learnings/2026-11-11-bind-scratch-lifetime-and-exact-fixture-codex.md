---
title: Bind scratch lifetime and exact fixture build provenance
author: Codex
date: 2026-11-11
branch: fix/rust-scratch-review-corrections
touches:
  - src/OpenBlade.DeploymentBroker/ScratchNativeReceiverLaunch.cs src/OpenBlade.DeploymentBroker/ScratchChildChannels.cs src/OpenBlade.Setup/WindowsScratchCandidateReadiness.cs build/Build-ScratchNativeReceiver.ps1 tools/openblade-windows-host/src/retained_process/tests.rs
---

## Decided
Bind both scratch child lifetimes to a private noninheritable kill-on-close job through the creation-time SDK job-list attribute, before authentication and resume. Concrete same-process Setup test callers use Process.Start; inspected .NET10 Process IL uses broad inheritance. Scratch channels therefore use noninheritable owner-only first-instance named pipes, with retained-created-child checks before secret release. Receipt, loaded/installed authority and exact process checks remain independent.

Build the private NativeAOT receiver from current working source with fixed fixture flags and the offline pinned SDK. Use source/configuration-derived external outputs, always copy, verify exact destination bytes and bind image/provenance hashes into the test assembly. Verify current source and copies before test runs. Keep data/temp siblings outside the enumerated code directory. Continuously drain bounded stderr and preserve primary plus cleanup refusals as exit3.

## Tried and ruled out
Create-then-assign leaves a parent-death gap. A launcher-private lock cannot coordinate unrelated broad-inheritance callers. Existence, native PE format, a self-computed closure digest and PreserveNewest do not establish source provenance. Disconnected original-resource counters cannot prove borrowed-handle behavior; tests now name actual injected ownership coverage.

## Watch out
Pure lifecycle and stream tests do not exercise native job/channel creation, crash behavior or process/token acquisition. Native execution remains a separately authorized gate. Retained capture/service verification is unwired; a future supplier must establish independent TOKEN_QUERY access without weakening checks or automatic elevation. Historical surviving-pipe/PID-reuse premises remain unproven. Never accept the scratch build pin as production trust.
