---
title: Internalize legacy capture evidence
author: Codex
date: 2026-10-18
branch: chore/internalize-legacy-evidence
cost: { minutes: 45, tokens: 683587 }
touches:
  - ".gitmodules"
  - evidence/provenance.json
  - tests/OpenBlade.Protocol.Tests/Rz090528HyperBoostWithoutCoolingPadAdmissionTests.cs
  - tests/Build.Tests/LegacyEvidenceProvenance.Tests.ps1
---

Removing the private captures submodule must preserve production admission bindings. Import only the exact sanitized Git blob bytes still consumed by code, tests, or current documentation under evidence/legacy-captures, and record each original path, pinned source revision, destination, and SHA-256 in evidence/provenance.json. Keep existing captures/annotations strings inside production manifests and typed gates as immutable provenance identifiers; map only test filesystem resolution to the core-owned path. New observations use Synapse Observer and carry their own reviewed provenance outside the fixed legacy map.
