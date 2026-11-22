---
title: Changelog-wide brevity rewrite
author: Jack
date: 2026-11-22
branch: claude/changelog-brevity-guidelines-d7fee1
cost: { minutes: 9, tokens: 81169 }
touches:
  - AGENTS.md
  - CHANGELOG.md
  - build/Validate-ChangelogReleases.ps1
---

## Decided
- Rewrote every CHANGELOG.md section, published ones included, to the AGENTS.md item 8 brevity rule. 235 KB to 183 KB, 784 bullets kept, headings, categories, links, credits and compare references unchanged.
- The AGENTS.md rule now allows rewording a published section if its heading, categories, bullets, links and credits stay. This supersedes the earlier note that published sections keep their wording.

## Tried and ruled out
- Worry that editing published sections breaks nightly generation: New-OpenBladeNightlyChangelogContent diffs each tag's Unreleased entries by exact text and copies older sections through untouched. Complete-NightlyRelease compares only the section of the tag being finalized.

## Watch out
- Do not reword a section whose nightly draft is still awaiting finalize-nightly.yml; its main section must match the attached snapshot.
- Rewriters drifted PR link hosts between OSSBlade and OpenBladeProject; a sorted link diff against the original catches it.
- Checks: bullet count, heading diff, link diff, Get-OpenBladeChangelogEntries parse, build/Validate-ChangelogReleases.ps1.
