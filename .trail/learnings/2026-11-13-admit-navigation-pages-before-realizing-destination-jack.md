---
title: Admit navigation pages before realizing destination cards
author: Jack
date: 2026-11-13
branch: feat/profile-card-navigation
touches:
  - src/OpenBlade.Tray/MainWindow.GlobalSearch.cs
---

## Decided
Check only native page admission before OpenGlobalSearchResult selects a destination. Keep full card, row, exposure and visibility admission in its deferred layout callback. This supersedes the earlier 2026-11-13 Guard shared card navigation learning's recommendation to run the entire result predicate before navigation.

## Tried and ruled out
The full early predicate blocked the existing Labs guides: they explicitly unhide the Keyboard section, but that visibility and its controls may realize only after page navigation. CI run 43413629635 exposed the Labs import failure. Page admission still prevents hidden lighting destinations from blanking the window, while deferred result admission preserves explicit reveal behavior.

## Watch out
Verify the complete caller set when changing shared navigation. The repaired native harness passed compact/wide profile links, hidden-page protection, standard/compact global search, and both Labs import/60 Hz links. Profile layout interaction checks use an explicit page width after image capture so hosted desktop constraints do not change what is exercised or alter snapshot pixels.
