---
title: Changelog brevity rule in AGENTS.md
author: Jack
date: 2026-11-22
branch: claude/changelog-brevity-guidelines-d7fee1
cost: { minutes: 1, tokens: 82300 }
touches:
  - AGENTS.md
---

## Decided
- Changelog tone rule lives in AGENTS.md Change workflow item 8, right after the PR-link sentence: bullets for laptop owners, 1-3 short sentences, keep required reader steps and real limits once, say "experimental" where it applies, move admission/readback/recovery/validation detail to the PR or guide, no disclaimer phrasing. Published release sections keep their wording.
- It points to the README and UI language section instead of duplicating it.

## Tried and ruled out
- No earlier tone rule for CHANGELOG existed; the README/UI language section never named the changelog.

## Watch out
- Existing Unreleased bullets are still long; trimming them is a separate change.
- AGENTS.md is CRLF; check CR count equals line count after Edit-tool edits.
