---
title: Reveal customized destinations when following profile links
author: Jack
date: 2026-11-13
branch: feat/profile-card-navigation
touches:
  - src/OpenBlade.Tray/MainWindow.ProfileNavigation.cs
---

## Decided
Treat profile-summary clicks as explicit destination requests, matching Labs guides: restore a section hidden in Customize before navigating and scrolling to its card. Reuse pageSectionPresentations for native cards and ancestors. The three Lighting section IDs bind their existing typed section options even before the template is realized. Admit the page before changing preferences.

## Tried and ruled out
Customize removes hidden presentations from their host; merely opening the native page leaves the requested card absent. Do not bypass capability visibility or invent another card. Restoring the presentation option preserves native support and edit-admission rules.

## Watch out
The native compact and comparison checks hide Fans and Keyboard Lighting, follow profile links, and verify restoration and navigation. The separate hidden-navigation test must still leave Profiles visible. Keep the existing shared deferred card checks and Labs reveal behavior.
