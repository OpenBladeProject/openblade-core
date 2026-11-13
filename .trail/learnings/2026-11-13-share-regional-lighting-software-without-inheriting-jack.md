---
title: Share regional lighting software without inheriting model semantics
author: Jack
date: 2026-11-13
branch: fix/issue957-iso-lighting
touches:
  - ".gitattributes"
  - CHANGELOG.md
  - docs/blade16-regional-lighting.md
  - src/OpenBlade.Contracts/AmbientLightingFrame.cs
  - src/OpenBlade.Contracts/Blade16KeyboardLightingLayout.cs
  - src/OpenBlade.Contracts/Fixtures/Blade16KeyboardLightingLayout.json
  - src/OpenBlade.Contracts/OpenBlade.Contracts.csproj
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json
  - src/OpenBlade.Protocol/ImportedSequenceLightingEffects.cs
---

## Decided
Issue #957 covers every source-selected keyboard region, not only German. Independently inspect SO6120/02E0 and SO6110/02C6 before sharing software. Both exact images contain six matching descriptor maps, but their transports and legacy canvas meanings differ. Preserve 0528 Right Alt at logical91 and project it to physical94/95; its source91 is a renderer-skipped Space placeholder. The shared projection consumes each model's own pinned map, caches layout metadata only per admitted session, and preserves frozen serialized canvases. Reserved transient IDs cover ISO and Japanese key events; Windows' pure HID-to-Set1 translator independently verifies their scan codes. Source and physical status remain separate.
## Tried and ruled out
Other supported lighting paths already use separate source maps or full6x16 grid output, so the all-model audit does not justify copying the6x17 mask elsewhere. Do not make a source-report hash portable by guessing its line endings: the0581 report pin18771140 coversCRLF; declare eol=crlf. The0528 report pinF8F0BA8D coversLF; declare eol=lf. Preserve each source pin and verify actual bytes. Legacy calibration palettes may preserve appearance without owning hardware admission.
## Watch out
Treat shared IPC frames as the stable logical format, then project at the final physical write boundary. Keep per-key frames distinct from spatial samples so unspecified keys stay black. Bind both spatial and transient-key routing in source admission fingerprints. Read metadata before mutations, not as a US-only equality gate. A refreshed main advanced0.60.1, so this patch must become0.60.2. Trail's historical keyword matches are not semantic conflicts; exact-source proofs, source fixture hashes and the unchanged transports address the actual constraints.
