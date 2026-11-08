---
title: Validate bundled notes across empty Unreleased boundaries
author: Jack
date: 2026-11-08
branch: chore/refresh-rust-status-cli
touches:
  - tests/OpenBlade.Tray.Tests/WhatsNewReleaseTests.cs
---

## Decided
When refreshing PR #899 onto main 71193867, Windows CI found a stale bundled-changelog test: the nightly changelog automation legitimately emptied Unreleased, while the development assembly version had no exact published section. Keep WhatsNewRelease's existing empty result and exact-version precedence. Validate readable bundled notes against the latest published heading discovered in the embedded asset, and cover empty Unreleased beside an exact nightly section.

## Tried and ruled out
Do not populate development notes with another release's history or add artificial Unreleased text to satisfy a test. Do not rerun the failed head as if this deterministic Assert.NotEmpty failure were transient. The failure is unrelated to the Rust CLI; all 67 Rust tests, four native lanes, 762 contract tests and 16,296 other Windows tests passed on that head.

## Watch out
The first refresh head fe3b9869 and its failed CI run 42509831912 remain evidence. Record the later test-only fix separately, retain the original Claude review for unchanged CLI source, and keep the developer CLI PR draft and unmerged. This shared test fix applies to every build following an empty-Unreleased nightly boundary; it changes no product or hardware behavior.
