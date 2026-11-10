---
title: Package the pinned Windows Rust roles offline
author: Jack
date: 2026-11-10
branch: feat/rust-phase-9a-candidate
touches:
  - build/development-deployment/RustFixtureCandidate.ps1
  - docs/windows-rust-development-service.md
  - tests/Build.Tests/Test-RustFixtureCandidate.ps1
---

## Decided
Keep Phase 9a offline and separate from Publish and Setup. Package the exact ordinary Phase 7e Service and Session Agent bytes under their fixture names. Require independent role pins, native x64 PE identity, a fixture-only P-256 signature and a detached receipt for the entire ZIP digest and ephemeral public key.

## Tried and ruled out
The pinned pair at artifact revision 41a3237e81188ed0780e89a2ca2d170da7d61db0 matched the archived ordinary outputs. Repacking with missing, extra, misnamed, changed or swapped entries did not satisfy verification even with a recomputed outer digest. A valid local signature did not admit a false architecture. The production validator still required the managed role files.

## Watch out
The offline archive does not confer release trust, installed readiness or registration authority. The separate native SCM target is VM-only. Require real LocalSystem SCM and least-privilege logon entries, process-bound typed readiness, independent installed readback and rollback verification before an installed cutover. Trail lexical matches on version, fixture, backend and architecture did not reflect behavior changes. Publish and Setup have no new callers or inputs.
