---
title: stabilize native slider accent fringes in translator snapshots
author: Jack
date: 2026-11-07
branch: feat/global-settings-search
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - OpenBlade.slnx
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - build/Capture-LocalizationSnapshots.ps1
  - build/CiImpactManifest.json
  - build/Publish.ps1
---

## Decided
CI run 42211379181 on global-search PR #883 retained two translator archives. Their only payload difference was two opaque pixels on the application-editor slider thumb: white versus RGB 247/255/247 after four-bit quantization. Normalize only mixed near-white endpoint bins beside at least three opaque white neighbors and two saturated accent neighbors. Keep the achromatic filled-corner path and byte-exact archive assertions. Bump the snapshot render profile to v5 in the renderer, capture manifest, and contract tests.

## Tried and ruled out
A global high-bin remap would change neutral F7 selection shading and introduce a new quantization boundary. Extra frame waiting would not resolve a stable rasterization difference between fresh processes. Compare retained ZIP payloads and pixel neighborhoods before changing capture timing or product UI.

## Watch out
Preserve thin glyphs, neutral selection shading, isolated pale detail and colored chart strokes. Use the retained 9x9 crop in all four orientations as a regression and run the same-process and fresh-process translator package checks. Refresh the PR version against latest main; after merging main at 0.53.0 this feature is 0.54.0.
