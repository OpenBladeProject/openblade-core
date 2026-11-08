---
title: Verify codec review fixes without broadening the migration stage
author: Jack
date: 2026-11-08
branch: feat/rust-report-codec
touches:
  - docs/rust-boundaries.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-protocol/tests/report_parity.rs
  - rust/crates/openblade-protocol/tests/report_properties.rs
  - tests/Fixtures/Parity/README.md
---

## Decided
The stage3 Claude advisory review completed successfully at base b75eceee4216391b042d09509c16814576bfa62c and original head3033eda7378f43ba643af4e3402454f3a365126f, using Opus5.5/high. No codec correctness bug was found. Confirmed follow-ups update stale migration docs, reject incoherent response-fixture requests/expected lengths with case IDs, exercise serde features/default-feature/removal and external-package admission guards, and guarantee fuzz successes with both None and matching Some(length). Verify the resulting head separately; it does not inherit the original head's Claude review.

## Tried and ruled out
IDs retain the authoritative C# ordinal uniqueness semantics; trimming them would change the fixture contract. Repeated parsing of a tiny test file has no demonstrated material cost. The private bounded encode/copy paths have checked invariants and tests; speculative future callers or deduplication preferences do not establish a current defect. The current reviewed test list already matches sorted order; any new target needs a separately deliberate guard update.

## Watch out
Guard negative tests use16 real offline manifest mutations plus2 replayed real-metadata snapshots for external name/source rejection, with temporary Cargo interception removed in finally. Keep these metadata policy tests distinct from actual dependency resolution and retain real Cargo checks afterward. All29 canonical report expectations and all C# production/baseline files remain unchanged.
