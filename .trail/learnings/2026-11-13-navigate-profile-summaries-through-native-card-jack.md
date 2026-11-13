---
title: Navigate profile summaries through native card navigation
author: Jack
date: 2026-11-13
branch: feat/profile-card-navigation
touches:
  - CHANGELOG.md
  - artifacts-profile-build.log
  - artifacts-profile-focused.log
---

## Decided
Use native Button wrappers around the existing comparison-row Borders for profile-summary navigation. Reuse OpenGlobalSearchResult for page selection, deferred layout, focus, BringIntoView and arrival feedback. Both compact and wide summaries map the same invariant fields to their native cards. Sort compact comparison options with the active source first; select that source when Profiles opens or enters compact layout.

## Tried and ruled out
Mouse-only Border taps lose keyboard and UI Automation access. Profile review must not change the active power source or apply settings. Trail check's lexical warnings match preserved comparison ContentTemplates, grid columns and unrelated historical notes: these changes retain those existing rows and wrap them in named native buttons, without changing drag capture, layout columns, input selectors, or hardware admission.

## Watch out
Profile rows can describe unsupported settings; navigation may open the native page while its unsupported card stays hidden. Keep capability visibility authoritative and exercise rendered layouts as well as XAML contracts. Use the existing disconnected localization harness, never a live hardware client, to verify navigation and scroll destinations.
