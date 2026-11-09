---
title: Monolith app and matching logo integration
author: Jack
date: 2026-10-29
branch: feat/monolith-branding
touches:
  - ".gitattributes"
  - AGENTS.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - build/identity/Assets/Square150x150Logo.png
  - build/identity/Assets/Square44x44Logo.png
  - build/identity/Assets/StoreLogo.png
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Installer.ico
---

## Decided
Monolith is the approved application and flat-logo direction. Keep the dimensional PNG for About, default notifications and installer header; use themed flat marks in README headers. Canonical geometry lives in openblade-branding tools/monolith_geometry.py. Copy all eight reviewed exports together and update app-assets.json hashes; SVG copies retain LF for byte parity. Version 0.44.2 on current main advances to 0.44.3 for this presentation-only patch.

## Tried and ruled out
Do not replace tray-state, mode-notification or engineering-glass artwork while updating the app identity. Do not paint logo keyboard details white: use actual transparent cutouts. Do not stretch tiny icons horizontally; uniform optical scaling preserves the approved 16:10 display.

## Watch out
The Brand directory is ignored by default; explicitly allowlist matching SVGs and provenance files. Installer authoring pins the installer ICO SHA-256. Keep the established <=48px installer fallback. Trail check lexical matches on OpenBlade file names and old 0.41.x version notes are unrelated to this asset-only update; it changes no DriverStore, hardware, THX or keyboard runtime contract. Exclude user-owned AGENTS.md changes from the PR. Rendering and package validation do not update the installed app.
