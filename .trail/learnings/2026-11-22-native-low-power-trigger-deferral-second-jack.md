---
title: "Native low-power trigger deferral, second review corrections"
author: Jack
date: 2026-11-22
branch: feat/native-trigger-low-power-gate
touches:
  - docs/windows-native-thermal-mutation.md
---

This supersedes parts of
`2026-11-22-native-low-power-trigger-deferral-review-jack.md` after a second
review. Each superseded statement is quoted.

## Decided

- Supersedes "Four mutants of the final tree survive as equivalents". Only
  two are equivalent:
  - the save's `!pending`: the save path refuses a fenced store first;
  - `ran`'s `!deferred`: the synthetic deferred step is Blocked, so
    `applied` and `unchanged` are already false.
- The unfinished arm's `!possible` and `!storage.pending()` are not
  equivalents. "A held write past the bound leaves both possible and
  pending" is true only for that one path. Two tests now pin them:
  - `a_possibly_submitted_step_unfinished_at_the_bound_is_not_a_deferral`:
    a Confirmed chain whose worker has not handed back at the bound, with
    the gate set meanwhile. It replies status 3 "... did not finish within
    its budget." and the debt settles.
  - `a_fenced_step_unfinished_at_the_bound_is_not_a_deferral`: a changed
    override document fences the store with nothing submitted. It replies
    "... no thermal setter was submitted." and the debt settles.
- Supersedes "The perf step after resume re-reads firmware and confirms
  without a setter, so it journals nothing". That holds only while firmware
  still holds the target. After a firmware reset in standby, the retry
  re-applies under a new journal id. That is a fresh application after a
  fresh read, not a replay.
- An unfinished-arm deferral no longer sets `unfinished`. Previously, a save
  whose confirmed first step was followed by a second step deferred at the
  bound replied 3, from `unfinished && submitted` in `into_outcome`. The
  same outcome deferred at the step's check replied 0.
  - `unfinished` now means "unfinished and not a deferral: its worker may
    still submit".
  - `a_save_deferred_at_the_bound_replies_as_one_deferred_at_its_check`
    pins this; the mutant restoring the old order dies.
- A moved gate generation proves only that a boundary arrived during the
  step. A new resume more than 2 s after the last boundary
  (`NEW_RESUME_TICKS`) moves it by two without a set. Labelling a step
  refused across that resume a deferral is benign: nothing was submitted
  and the debt stays owed.

## Watch out

- Round 2 left 20 tests in `request_triggers_low_power.rs`, not the 22 it
  reported; there are 23 now.
- Two debt drops remain, unchanged from before the low-power slice.
  - A possibly submitted step that has not handed back by the bound ends
    the reconciliation, so a later class is not applied after resume.
  - A fencing notice that does not move the generation drops the debt, for
    example a repeated resume, or a power-status change inside the first
    step after the gate cleared.
  - Both are C3 follow-ups (resume/DisplayOn reconcile of owed pairs). A
    dropped debt recovers only through C3's lease-expiry or power-change
    triggers, or the next changed report.
- The probe-style `hold_first_retirement` blocks the worker thread inside
  `on_mutation_retired` until released. Each such test waits the full
  12 s bound.
