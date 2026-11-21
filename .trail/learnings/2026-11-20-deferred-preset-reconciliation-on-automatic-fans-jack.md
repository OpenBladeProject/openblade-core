---
title: Deferred preset reconciliation on Automatic fans
author: Jack
date: 2026-11-20
branch: fix/manual-to-automatic-fan-restore
touches:
  - "src/OpenBlade.Service/ControlCoordinator.cs#ReconcilesPerformanceOnAutomaticFans"
  - "src/OpenBlade.Service/ConfirmedStateDriftTracker.cs#PerformanceChangedSinceIntent"
  - "src/OpenBlade.Service/ServiceControlRuntime.cs#ManualFanRangePreset"
  - "src/OpenBlade.Service/ServiceControlRuntime.cs#DeferredPerformanceIntentMessage"
---

## Decided

- On reporting backends without direct manual transitions (`ReportsPerformanceWithManualFans && !SupportsDirectManualFanTransitions`, `ControlCoordinator.ReconcilesPerformanceOnAutomaticFans`), one rule covers every fan request that ends in Automatic, including retries and Automatic to Automatic: reconcile the preset to the resolved intent from the fan step's fresh readback, unless that readback differs from the preset held when the intent last became authoritative.
- That reference is `ConfirmedStateDriftTracker.performanceAtIntent`. It is set by the first observation, a matched OpenBlade performance write, and `RecordExplicitPerformanceIntent()` after any explicit performance choice or clear (deferred or applied, including the shortcut). It orders an external change against the user's last choice without clearing the user-visible external-change latch.
- Preset choices under manual fans on these backends are stored on every power source, battery and unclassified included. Shared selectors keep the external-power-only deferral, so their transcripts stay identical to main.
- Manual fan range, curve maintenance and fixed-speed changes between manual modes follow the freshly read applied preset (`ManualFanRangePreset`), not the deferred intent, so a curve keeps following temperature while a choice waits.
- The deferral decision on the override path resolves the stored intent with `ResolveAutomationDesired`, so clearing an override back to a Custom profile applies Custom through the fan handback.

## Tried and ruled out

- Using the external-change latch alone as the exit gate. It suppressed a newer user choice after any earlier external change, and it missed an external change that no observation had latched yet.
- Clearing the latch on an explicit choice. That hides a real external change from the UI; recording ordering needs only the reference preset.
- Deciding the exit from the previous fan mode. A failed first exit left fans Automatic in the desired state, so the retry skipped the restore.

## Watch out

- Profile edits (`UpdateProfilesAsync`) do not record an explicit intent; an external change seen before a profile edit is still treated as newer.
- The reference tracks the preset only, not Custom CPU and GPU levels.
- When changing this, rerun a base-versus-branch transcript of the shared-selector shapes (SafeTargetDefaults with and without direct transitions, and experimental models forced to `ReportsPerformanceWithManualFans = false`); they must stay byte-identical.
- After restoring files from a tar backup, touch them: tar keeps old mtimes and the incremental build silently reuses the stale assembly.
