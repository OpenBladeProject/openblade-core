---
title: Reveal collapsed search rows through their enabled expander
author: Jack
date: 2026-11-06
branch: feat/global-settings-search
touches:
  - src/OpenBlade.Tray/MainWindow.GlobalSearch.cs
  - src/OpenBlade.Tray/MainWindow.GlobalSearchCatalog.cs
---

## Decided

Search THX Enhancement rows through the visible expander and each feature's availability predicate. Expand the enabled owner before resolving the named row, update layout, then reveal the row. Search can bring a disabled section's header into view while preserving its collapsed state.

## Tried and ruled out

Using the Voice clarity row as its own availability anchor dropped it in a disconnected fixture with the expander collapsed. A cold THX page with synthetic state also lacked a complete parent path even at the expander. The focused production-shaped fixture first visits THX, loads the disconnected audio state, leaves Enhancements collapsed, and searches from Settings. It verifies the expander opens and Voice clarity is fully inside the viewport. The legacy fixture verifies that the disabled Applications expander stays collapsed.

## Watch out

Apply the same lighting brightness predicate before template realization as the XAML uses after realization. Closing search during reversible window detachment clears its suggestion cache so a retained query can be rebuilt when focus returns. The fixtures exercise navigation and presentation with disconnected audio state and do not establish device admission.
