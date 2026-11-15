---
title: Resolve shared driver aliases and canonical cache locks
author: Codex
date: 2026-11-15
branch: fix/pawnio-install-diagnostics
touches:
  - src/OpenBlade.Platform/WindowsDriverServiceImagePath.cs
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - src/OpenBlade.Platform/WindowsRazerDriverApiDiscovery.cs
  - tests/OpenBlade.Platform.Tests/WindowsRazerDriverApiDiscoveryTests.cs
  - build/NativePayload.ps1
  - tests/Build.Tests/NativePayload.Tests.ps1
---

## Decided
Claude Opus 5.5 reviewed PR 975 at 18bc83265cf9bc8d779e2ac74ea965cd8491632a against 5975b45ff36ac33670b4e4cab2165436ca3cc26c. Reproduce actionable review claims before changing code. A shared Windows driver-image spelling resolver now rejects unscoped relative paths before process-directory resolution, retains the rooted-SystemRoot-suffix guard, and supports the bounded kernel aliases in both callers. PawnIO still requires canonical direct DriverStore package paths; Razer still requires its protected drivers directory and exact service filename. Trust, device and model admission remain separate.

On Windows, fold the shared Cargo directory and mutex identity to lowercase together. Support local origins ending in a Git directory and URI paths without treating a query as a repository name. Preserve one shared cache per repository name. Metadata test fixtures must restore global LASTEXITCODE even on failure and exercise unsafe names.

## Tried and ruled out
New regressions failed before the fixes: six Razer path cases and the mixed-case Cargo identity assertion. All 223 Platform tests and the native packaging metadata contracts pass after the fixes. An independent follow-up review found no actionable correctness issues. Owner-qualified caches conflict with the shared-cache policy; source fingerprints and the existing publication mutex remain required. Duplicate path diagnostics preserve evidence before service-configuration rejection and for direct path validation. Admission runs once per CPU helper session, not for each temperature sample; no measured logging regression justified discarding default-listener diagnostics.

## Watch out
The earlier empty logging note and compressed numeric spacing remain immutable history. This complete note supersedes the incomplete record; current reports use readable version and test counts. The missing original reporter ImagePath still prevents attributing their exact installation failure. Software alias regressions and signed-file read-only reproduction do not establish end-to-end reporter recovery. Do not mutate a shared driver service to manufacture a reproduction. Commit native build-script changes before packaging so provenance binds the exact input bytes.
