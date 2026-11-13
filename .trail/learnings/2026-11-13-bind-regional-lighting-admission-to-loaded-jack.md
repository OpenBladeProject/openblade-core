---
title: Bind regional lighting admission to loaded source bytes
author: Jack
date: 2026-11-13
branch: fix/issue957-iso-lighting
touches:
  - src/OpenBlade.Contracts/Blade16AmdKeyboardLightingLayout.cs
  - src/OpenBlade.Contracts/Blade16KeyboardLightingLayout.cs
  - src/OpenBlade.Protocol/ProductionLightingEvidence.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
  - src/OpenBlade.Protocol/Rz090528RegionalFrameProjection.cs
  - tests/OpenBlade.Contracts.Tests/PublicApiBaseline.txt
  - tests/OpenBlade.Protocol.Tests/ProductionLightingPortTests.RegionalLayouts.cs
  - tests/OpenBlade.Contracts.Tests/Blade16SourceFixtureIdentityTests.cs
---

## Decided
Source-backed lighting admission must compare the approved fixture hash against SHA-256 computed from the loaded resource bytes, separately from the routing fingerprint. Keep the 0528 legacy unowned pulse 94 as a no-op at the port boundary, preserving older clients without assigning it a new key or firmware address.

## Tried and ruled out
The completed Claude review of 22bf1574 raised ten candidates. Confirmed the source-hash binding gap and legacy pulse compatibility issue. Its mirror concern does not match the contract: the cooling-pad sampler consumes columns of the successfully submitted physical grid, not serialized logical key identities. Preserve physical projection there. A read-only 00/86 source query does not establish a transition-write prerequisite. Capture-only admission was deliberately replaced by exact-model source authority.

## Watch out
The immutable source fixtures deliberately fail closed on changed bytes; retain their explicit CRLF/LF attributes. Independently reviewed model descriptors remain separate despite similar parsing code. Six verified transient scan bindings, source introspection APIs, bounded cached projection construction, and frame-copy ownership are intentional; optional cleanup is not a correctness fix. One completed real Claude review plus verified supported fixes satisfies this task; do not launch another metered review automatically.
