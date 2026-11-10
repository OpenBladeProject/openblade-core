---
title: Finish branch cleanup before handing back
author: Jack
date: 2026-11-10
branch: docs/always-clean-up-branches
touches:
  - AGENTS.md
---

## Decided
Finish branch cleanup for every completed task, including local-only work. Verify branch tips and linked worktrees, remove the clean task-owned checkout, and verify retired local and remote refs are gone before handing back.

## Tried and ruled out
A missing upstream is insufficient evidence for deletion. Confirm landed tips against refreshed origin/main and check open pull requests and linked worktrees. Do not use force to get around a failed cleanup check.

## Watch out
Preserve active checkouts, backup refs, artifacts, and unique commits. If cleanup cannot finish safely, report the specific blocker.
