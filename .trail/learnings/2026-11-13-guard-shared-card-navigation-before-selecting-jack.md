---
title: Guard shared card navigation before selecting hidden pages
author: Jack
date: 2026-11-13
branch: feat/profile-card-navigation
touches:
  - src/OpenBlade.Tray/MainWindow.ProfileNavigation.cs
---

## Decided
Check IsGlobalSearchResultAvailable before OpenGlobalSearchResult changes the selected navigation item. Every caller, including profile links, shares this admission. Profile links target the native card; global search may additionally target a nested row. Keep the original row Border in a native Button whose transparent content-only template adds activation and system focus without repainting row surfaces. Row automation names bind to existing localized labels and complete profile AccessibilityDescription values.

## Tried and ruled out
A deferred availability check is too late: selecting a hidden Lighting navigation item can collapse all page content. Reusing a .Text localization resource directly on AutomationProperties.Name fails the resource-property contract. x:Bind unqualified function expressions generate instance calls, so these formatting helpers must be instance methods. Comparisons across wide and compact layouts should compare destination sets because their visual row grouping intentionally differs.

## Watch out
Retain keyboard access to pending-value tooltips. Native subsystem pages continue to show their active-profile controls; navigation does not select another power source or apply a reviewed profile. Card visibility remains authoritative. The native disconnected harness checks hidden destinations and all admitted row destinations before release.
