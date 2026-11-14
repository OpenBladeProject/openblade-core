---
title: Qualify native Agent battery report Trail phrase matches
author: Jack
date: 2026-11-14
branch: feat/native-agent-battery-report
touches:
  - ".gitattributes"
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/battery_report.rs
  - rust/crates/openblade-platform-windows/src/battery_report/custody.rs
  - rust/crates/openblade-platform-windows/src/battery_report/process.rs
  - rust/crates/openblade-platform-windows/src/battery_report/tests.rs
  - rust/crates/openblade-platform-windows/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/Cargo.toml
---

## Decided
The Agent battery-report exact-base Trail check remains exit 1; preserve its JSON and semantic triage. A default-branch comparison included 500 historical files and a truncated diff, so it is not evidence about this selected slice. Checking accepted a3454f5a first found 12 phrase matches in tracked package/pipe tests; the complete staged slice then reported 20 matches in the new provider and custody code. These are advisory lexical findings, not a green policy gate.

The new operation reads a report and never restores durable battery overrides, uses GPU/display/HID commands, revises historical producer identity, or changes native package selection/rollback. Process completion means original root exit plus zero active job processes, not root exit alone. Stop, timeout, disconnect and stale origin await the same supplier. Suspended job creation and read-only retained-handle reconciliation preserve uncertain ownership. Caller identity and installed origin remain independently verified; a known-folder output path is not origin evidence.

## Tried and ruled out
Do not delete or rewrite prior learnings, weaken trust, discard rejection controls, infer historical drain, or suppress the reported gate to remove shared noun matches. Service capability tests explicitly reject cross-role capability forgery; the Agent feature grants only its implemented read-only report endpoint.

## Watch out
Keep the nonzero checker result and these reasons in the commit and review packet. Its capped phrase list is not an exhaustive semantic proof. Actual native frames through the strict managed Battery.v1 codec, injected cancellation/lifetime tests, exact output-object tests and one inert descendant test provide bounded software evidence. No installed endpoint, actual powercfg, product role, SCM, installation or hardware execution was performed.
