---
title: Use the app icon consistently in README headers
author: Jack
date: 2026-10-29
branch: docs/readme-app-icon
touches:
  - AGENTS.md
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - src/OpenBlade.Tray/Assets/Brand/APP-ASSETS.md
---

## Decided
README headers in English, Chinese, and Russian use the existing dimensional OpenBlade.png app icon at 128 pixels on both light and dark backgrounds. The user chose the app icon over the separate flat logo for recognition and consistency with Windows and About. Keep APP-ASSETS.md aligned with these consumers.

## Tried and ruled out
Theme-specific flat marks in README were superseded by the user's app-icon choice. Do not recolor or regenerate the app icon for README; use the same committed asset. The flat SVG marks remain available for other branding uses.

## Watch out
This is documentation-only: preserve production asset hashes, version, installed binaries, and unrelated AGENTS.md edits. The local headless light/dark header preview and README hardware-support contract passed. No reinstall is needed.
