---
title: Own partial native display query observations and both feature drains
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
  - tools/openblade-windows-host/src/session_native/feature_lifetime.rs
---

## Decided
The installed native Agent agent-display-query-v1 selection serves real read-only CCD/refresh/HDR/WCS observations through the original Origin and fixed current-user Display.v1 endpoint. Its authenticated capability is session-display-query-partial-v1, combined exactly with agent-battery-report-v1 when both are selected. Managed protocol2 decoding and both package consumers agree. Brightness stays null/empty and the detached WMI worker is never started. Default managed selection is unchanged.

The nonnullable v2 HDR fields cannot represent Unknown. This partial native query returns Succeeded with an unavailable snapshot and a specific message on HDR failure, rather than claiming HDR Off or selecting an unobserved SDR default. Missing refresh keys and absent ICC associations remain independent observations. CCD observations resolve their own indices; compare the resolved display identity and state around WCS and discard changed observations, without claiming an atomic OS snapshot.

Own each started spawn_blocking read and await its actual completion on cancellation, deadline, disconnect, origin loss and Stop. Cancellation cannot interrupt an arbitrary synchronous CCD/WCS call, so positive retirement is conditional on the call returning. Combined feature supervision broadcasts Stop and joins both feature drains even when the first drain fails.

## Tried and ruled out
Do not compose ScriptedDisplay or a permanent denial as production suppliers, start detached WMI for this slice, guess HDR Off to read an ICC default, truncate identities into valid keys, drop a losing feature future, or infer installed runtime and physical validation from injected tests. Do not run suppliers, product roles, powercfg, installation or hardware in this offline stage. Native request parsing is intentionally query-only; Display's existing managed decoder has no explicit Battery-style duplicate-member scan.

## Watch out
The two-feature test uses injected blocking feature futures in the production supervisor, while separate dispatcher tests inject display suppliers and peers. These are bounded software evidence, not real combined endpoint execution. Preserve fresh current-message SID/session/LUID and origin fences, connection-local peer rejection and causal finite diagnostics. Later work still owes owned WMI retirement, automation-recovery behavior, display mutation/automation, current-main reconciliation and runtime/cutover verification. The parent orchestrator owns frozen Claude review and acceptance.
