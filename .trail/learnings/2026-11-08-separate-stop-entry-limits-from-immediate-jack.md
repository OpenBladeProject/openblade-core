---
title: Separate Stop entry limits from immediate cutoffs
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-stop-auto-guardian.md
---

## Decided
Keep entry admission distinct from continuation limits when interpreting Stop trials. Custom AdmitsEntry first enforces the immediate cutoffs, then requires fresh instantaneous CPU/GPU readings at or below their normal limits. A rolling-average limit is therefore also the current instantaneous entry limit; changing only the CPU immediate cutoff does not alter that guard.

## Tried and ruled out
The authorized installed fc54c108 CPU65-average / CPU80-immediate trial passed preflight at52/53/53 C, but the guardian measured CPU70 C against EntryLimit65 C before any EC command. The80 C immediate cutoff did not fire. Fans stayed Automatic, the unused owner released, and exact original settings including the CPU cutoff were restored. Do not describe this as Stop, a rolling-average exit, or Auto-after-write restoration.

## Watch out
Save the actual original settings at execution and restore the whole record after independent release proof. Keep the instantaneous entry preflight unchanged when testing only a cutoff change. Removing or changing the entry rule is a separate product behavior change; do not silently bypass it to make an authorized trial succeed. Preserve the actual decision sample rather than substituting later cooler service telemetry.
