---
title: Track power-only block provenance without latching it
author: Jack
date: 2026-11-14
branch: fix/issue-967-ac-control-availability
touches:
  - evidence/source-reviewed-blades/2026-11-14-unclassified-external-validation/README.md
  - evidence/source-reviewed-blades/2026-11-14-unclassified-external-validation/claude-corrections-metadata.json
  - evidence/source-reviewed-blades/2026-11-14-unclassified-external-validation/claude-initial-metadata.json
  - evidence/source-reviewed-blades/2026-11-14-unclassified-external-validation/source-verification.json
  - evidence/source-reviewed-blades/2026-11-14-unclassified-external-validation/unclassified-external-laptop.png
  - evidence/source-reviewed-blades/2026-11-14-unclassified-external-validation/verification.json
---

## Decided
Track whether a published write block came only from power classification with explicit publication provenance. ObservationReason.None and a retained power enum are not enough to distinguish a power-only block from recovery or controller authority. Carry the marker across presentation-only revisions, preserve independently established non-power blockers, and let fresh classification discharge only the block it owns.

Use private fresh Windows presence under the operation gate for curve admission; publish completed unavailable classification so downstream advanced/profile controls cannot keep using a stale Full label. An owned Battery Saver fan stop follows the physical Windows battery scope and uses existing guarded fan-only Auto release on AC, without changing stored profile intent. Keep lighting ownership/yield bookkeeping ahead of profile deferral.

## Tried and ruled out
Do not restore a live-mutating GetStatus projection to repair stale publication. Do not treat a pending or canceled read as completed evidence. Do not infer that stale presence always causes Automatic handback; it can instead prevent target updates. A lifecycle-only restore caller does not prove the public profile-deferral bug is reachable through that caller.

## Watch out
Private publication hooks are invoked by meaningful revocation and snapshot tests through reflection. Preserve their exact existing parameter counts; optional parameters do not preserve reflection arity. Put provenance-bearing variants behind distinctly named helpers rather than ambiguous overloads. Assert exact curve targets and retained intent: the safety maximum may be represented by the admitted Maximum alias, so a Fixed-only assertion is not the contract.
