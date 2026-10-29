---
title: Pin approved deck refinement and recalculate patch version
author: Jack
date: 2026-10-29
branch: fix/monolith-deck-refinement
touches:
  - AGENTS.md
  - Directory.Build.props
  - build/identity/Assets/Square150x150Logo.png
  - build/identity/Assets/Square44x44Logo.png
  - build/identity/Assets/StoreLogo.png
  - src/OpenBlade.Tray/Assets/Brand/APP-ASSETS.md
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Installer.ico
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Mark.Dark.svg
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Mark.Light.svg
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.ico
---

## Decided
The approved Monolith deck refinement uses branding commit836546348ddd770c38bb620e24ab3305051f1a8b. Copy all eight exports together and pin the installer hash. Current main is0.44.3, so this small presentation fix advances to0.44.4. The dark-surface README logo uses a dark display, silver-gray deck and green curves; keyboard details remain true transparent cutouts.

## Tried and ruled out
Do not restore a flat white or gray screen block; the user explicitly requested component recoloring. No Contracts, IPC, service, Session Agent, hardware, tray-state or notification-state logic changes are part of this patch. Preserve unrelated user-owned AGENTS.md edits.

## Watch out
Trail-check matches against historical0.41.x/0.44.3 version notes are superseded by current-main patch arithmetic. Matches involving DriverStore and keyboard-white wording are lexical false positives for these image assets. User AGENTS.md changes are excluded from the commit. Use a separate committed-head tray-only build for installation and preserve installed shared assembly identity and protected service/Session Agent hashes.
