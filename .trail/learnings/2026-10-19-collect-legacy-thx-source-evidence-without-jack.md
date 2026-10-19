---
title: Collect legacy THX source evidence without promoting installation
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - CHANGELOG.md
  - build/CiImpactManifest.json
  - docs/thx-installation-investigation.md
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - build/Export-LegacyThxBindingEvidence.ps1
  - tests/Build.Tests/LegacyThxBindingEvidence.Tests.ps1
---

## Decided

Use a developer-only, read-only collector for exact RZ09-0482, RZ09-0483 and RZ09-0484 when original recovery images are unavailable. Preserve selected original INF/CAT bytes and hashes outside Git; keep the manifest sanitized and alias-based. Follow InterfaceSetting's ordered setting-name lists to each setting's own FX/0 and EP/0 source trees. Keep collection separate from provisioning and retain Partial when endpoint association or provenance is missing.

## Tried and ruled out

Existing THX diagnostics report admission but do not export the original binding source. The keyboard driver exporter is a different exact-device release tool. Windows-recorded extension configurations identify package and section leads, but do not prove application. Matching driver candidates and a reused machine-local OEM INF alias are insufficient installation provenance. A successful service/COM registration trace does not supply the missing endpoint FX binding.

## Watch out

Validate recorded identities against the current original INF before selecting a package. Historical SetupAPI aliases can be recycled; package iteration order is not installation order. Preserve typed CLSIDs and structural settings while excluding private instance identities and arbitrary paths. Missing SignTool, catalog membership, source sections or endpoint association must remain explicit gaps. Never execute vendor code, elevate, mutate registry/devices/audio, or infer a standalone install recipe from collection. Fixture success does not replace a working legacy capture or a clean test with Synapse absent.
