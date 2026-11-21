---
title: "Native Agent function-row role admission, expiry and parity"
author: Jack
date: 2026-11-21
branch: feat/native-agent-function-row
touches:
  - ".github/workflows/rust.yml"
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/agent_diagnostics.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/tests.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tests/OpenBlade.SessionAgent.Tests/OpenBlade.SessionAgent.Tests.csproj
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/build.rs
---

## Decided
- The native Agent function-row role (`agent-function-row-v1`) admits exact RZ09-0581 only and observes the Service; every action effect returns Unsupported with one diagnostic. Fn is never observed natively, so no press is admitted and the no-op effects cannot swallow a key.
- Driver mode, primary mode and device are read only while `deviceVerified`, matching `ServiceReactiveLightingLoop.ObserveStatus` (unverified resets all three).
- The hook's admission expires: a status is admitted until its 2 s freshness ends, and a 500 ms tick that re-verifies the origin renews it to 1 s. The idle status subscription has no heartbeat, so the renewal stands in for it; reconnect, stale status, origin loss and stop invalidate at once.
- The ownership guard refuses the hook while either Razer-driver journal exists or cannot be observed. Stricter than managed, which parses the journal and allows later-boot or non-FunctionRow journals.
- Managed parity lives in `tools/openblade-windows-host/tests/data/function-row-hook-vectors.json`, recorded by `ServiceReactiveLightingLoopTests.NativeFunctionRowVectors`; the domain `function_row_vectors` test lists the only two native differences.

## Tried and ruled out
- A profile-fallback difference for an absent `functionKeyPrimary`: the safe-default profile records null in managed too, so the vectors do not exercise the fallback.

## Watch out
- Managed treats an unknown Fn as released and admits a press seen first with wasDown; native refuses both (`fn/unknown`, `sequence/started-mid-press`).
- The Bash tool collapses doubled backslashes inside heredocs; write scripts that contain backslashes with the Write tool or `String.raw`.
- Git Bash `sed -i` on a CRLF working-tree file can rewrite it as LF; check `git ls-files --eol` after editing C# or csproj files.
- A thread-local counting `#[global_allocator]` in `function_row_hook/tests.rs` covers the whole platform lib test binary; it counts only threads that opt in.
