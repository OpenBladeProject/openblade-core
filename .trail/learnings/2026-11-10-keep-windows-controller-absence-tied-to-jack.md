---
title: Keep Windows controller absence tied to complete observations
author: Jack
date: 2026-11-10
branch: feat/rust-phase-5y-controller-presence
touches:
  - rust/crates/openblade-platform-windows/src/controller_presence.rs
  - rust/crates/openblade-platform-windows/src/process_snapshot.rs
  - tools/openblade-windows-host/src/performance_runtime/tests.rs
---

## Decided
Phase 5y at Windows Rust baseline e6a92c06f01f012879a5835e3607b48749ee86aa keeps the presence list, per-source scan warning, and provenance-aware HyperBoost list separate. An incomplete process scan retains exact known rows and disables process-based exclusions. The synthetic performance fixture can invalidate its existing controller-clear gate using the observation; fresh absence cannot re-grant fixture authority.

Pinned managed references at that baseline:
- src/OpenBlade.Protocol/ControllerPresence.cs:5-226 (eb823f8c674fbc776e4d57996cbf06a167ddec4e) and src/OpenBlade.Protocol/WindowsControllerProcessParents.cs:7-141 (a76dfeb73e3016f8277e353b40255f7ae41a7a10).
- src/OpenBlade.Protocol/WindowsHardwareAccessGuard.cs:64-172,225-244 (fda7e75a2f09cf96327c7b838c4e29794f4d4c6e) collects parent/process evidence and projects presence and warnings. src/OpenBlade.Service/Program.cs:48-51 (e94083bda9399ae1c2025f922989d65e7642ae2b) and ServiceComposition.cs:59 (e17c280c24112adef16c7633bc0796f2f59a101b) wire the shared monitor and guard. ServiceControlRuntime.cs:14967-15020 (d73eadd4fcc22e0885785c0437f58006ea4ff86b) refreshes service presence. ServiceControlRuntime.CpuTuning.cs:235,317 (52a80ede2568f7f3da77e5f38c552d0b7e899edc) observes directly for CPU tuning admission.
- src/OpenBlade.Contracts/RazerControllerServiceAliases.cs (0be416bac139e0f6daaade94a3df42648a193605), src/OpenBlade.Contracts/RazerThermalControllerPolicy.cs (77d2f8b077d4e1c30c225186db3d4c0e9980aa66), and src/OpenBlade.Protocol/SynapseBladeBlockerVerifier.cs (b844586126bea70f7f3a7b4632a6735d039adb88) define exact alias and verifier semantics.
- tests/OpenBlade.Protocol.Tests/WindowsControllerPresenceMonitorTests.cs:5-209 (19cd12df2f87a1508b82bde62cbd2cd1cc35ea18), tests/OpenBlade.Protocol.Tests/SynapseBladeBlockerDetectionTests.cs:343-367 (08ed1cc567139cff56f053b72cd7148b5f1e8386), and tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.cs:11169,12990 (53b3e60f5a81ab1f88a5bd1e75d815d6adcee4ef) pin matching, parent identity, refresh, and advisory warning behavior.
- The GPU Toolhelp source reused here is rust/crates/openblade-platform-windows/src/gpu_apps/sampling/native.rs:266-296 (f133373c4416b3629b60cd3396f3b6cdc478f2ae).

## Tried and ruled out
The GPU parent map can be partial and does not establish absence. Reuse the owned, bounded Toolhelp traversal and expose explicit completion to the inactive process inventory. The GPU consumer keeps its prior best-effort map. Its query-limited GPU identity path includes token/SID eligibility for a different contract.

## Watch out
A Toolhelp PID/parent pair cannot establish process-instance or image provenance. Exclusion proof must match the current PID, session and birth time. An absent, stale, duplicate or mismatched parent edge retains the helper, as does missing or conflicting LampArray evidence. The opt-in native process inventory is compile-checked only and provides names and raw parent links. Live service inventory, held-handle controller identity, patch and helper verification, and LampArray acquisition remain separate gates for any production integration.
