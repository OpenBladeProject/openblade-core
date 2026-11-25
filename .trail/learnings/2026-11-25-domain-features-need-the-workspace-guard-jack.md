---
title: Domain features need the workspace guard allowlist
author: Jack
date: 2026-11-25
branch: feat/native-agent-manual-brightness
cost: { minutes: 305, tokens: 1096914 }
touches:
  - captures/
  - rust/Check-Workspace.ps1
  - tools/openblade-windows-host/src/service_status/unserved.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/thermal_convergence_loop.rs
  - docs/windows-native-thermal-mutation.md
  - ".github/workflows/rust.yml"
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/thermal_reapply.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_convergence_loop.rs
---

## Decided
A new openblade-domain feature or integration-test target must also be admitted in rust/Check-Workspace.ps1: the exact domainFeatures string (name=sorted deps, comma-joined) and the expectedTests['openblade-domain'] list. The S5 convergence slice added 	hermal-convergence = ["thermal-history"] and 	ests/thermal_convergence_vectors.rs without either, so the workspace guard failed only after landing (fixed in 05d0bf56).

## Tried and ruled out
Local landing lanes with feature combinations CI never builds (export-only, or every service feature at once). They fail by design: 	he_compiled_identity_ends_with_the_refused_marker assumes CI's marker order, and export alone leaves dead code under clippy. Mirror the service lane list in .github/workflows/rust.yml instead.

## Watch out
Run ust/Check-Workspace.ps1 and the CI domain lanes (-p openblade-domain --features thermal-convergence, clippy with 	hermal-convergence,profile-set) on any branch that touches rust/crates/openblade-domain/Cargo.toml or its tests directory; the host crate's lanes never exercise them.
