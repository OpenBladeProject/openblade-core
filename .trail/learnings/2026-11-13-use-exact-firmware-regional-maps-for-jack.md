---
title: Use exact firmware regional maps for Blade 16 lighting
author: Jack
date: 2026-11-13
branch: fix/issue957-iso-lighting
touches:
  - ".gitattributes"
  - Directory.Build.props
  - src/OpenBlade.Contracts/OpenBlade.Contracts.csproj
  - src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json
  - src/OpenBlade.Protocol/ImportedSequenceLightingEffects.cs
  - src/OpenBlade.Protocol/Pid02E0MatrixRenderers.cs
  - src/OpenBlade.Protocol/Pid02E0MatrixReport.cs
  - src/OpenBlade.Protocol/ProductionLightingEvidence.cs
  - src/OpenBlade.Protocol/ProductionLightingFactory.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
---

## Decided
Issue #957's two unlit ISO keys come from applying the ANSI 6x17 physical mask to all RZ09-0581 keyboards. Exact SO6120 MCU1.07 (image90BBE0D8DD1CA31614A2465C7C8821347B2A667606C39BEDADBD59B98886344F) has six descriptor tables and an exhaustive byte selector. Read00/86 payload0, the same byte consumed by initialization; never infer this map from Windows language or the adjacent edition byte. Preserve the saved ANSI canvas, then project logical Enter66 and backslash49 to physical48 and64 on the applicable source map. Firmware descriptor and extended transport proof is in docs/blade16-regional-lighting.md.
## Tried and ruled out
Product736/710 UI DEVICECONFIG has placeholder MatrixPos[0,0] fields and cannot establish RGB addresses. Captured ANSI holes are not invariant across regions. A fixed00/86 reply[1,0,0] incorrectly constrains metadata to a US response. Do not copy SO6120 maps to another model without that model's independent source proof.
## Watch out
Physical lighting validation remains separate from static source review. Preserve exact-device and per-submission safety, source fixture hashes and legacy serialization. The version check may report historical numeric conflicts for a correct patch bump;0.60.0 to0.60.1 is the intended defect-fix increment. Actual regional selection comes from the layout byte, not from naming country layouts by edition numbers. Spatial colors for regional-only cells are local rendering choices; they do not establish input bindings.
