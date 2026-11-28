---
title: Native setColorProfile parity and its mutation gaps
author: Jack
date: 2026-11-19
branch: feat/native-agent-color-profile
---

## Decided
- Native setColorProfile (agent-display-color-write-v1, marker session-display-color-write-partial-v1) reuses the CCD write path with Fields::COLOR; the module keeps the ccd_write name although WCS now uses it.
- The reopening read (Snapshot::observes) drops color as well as refresh: every available snapshot completed the WCS read, so SDR with no active profile still repairs a paused gate. HDR on is refused before any submission.
- color_profile::write classifies by a submitted flag set only inside admit(), after the fence passes. A read failure before the first setter is Refused with no restoration; after it, Uncertain plus managed Restore (default, then remove the added association, then scope).
- Deliberate deviations from managed: prepare failures are Failed not Indeterminate, HDR Unknown is refused, readback requires the same PanelIdentity.

## Tried and ruled out
- Fence tests that only use the empty baseline leave the fence-after-default mutant alive: the first setter differs per baseline (association, scope or default), so test all three.
- Moving self.submitted = true before the fence check inside admit() is an equivalent mutant (a refusal ends the transaction); mutate the initial value instead.

## Watch out
- [IO.File]::ReadAllText with a relative path in PowerShell resolves against the process directory, not Set-Location, and can read the landing worktree's copy.
- IDE0055 and CS1718 analyzers fail the Deployment build for crude C# mutations; use a condition such as features.Length < 0.
- trail check on this branch flags only .gitattributes/.editorconfig lines inherited from the base, not the color change.
