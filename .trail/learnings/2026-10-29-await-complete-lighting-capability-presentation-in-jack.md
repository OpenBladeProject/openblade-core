---
title: Await complete lighting capability presentation in CI test
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - AGENTS.md
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
---

## Decided
ExperimentalLightingParameters_OfferOnlyAdmittedModesAndUpdateOnCapabilityChanges must await dispatcher completion after each Publish before checking lighting presentation or editing its draft. Capabilities is assigned before ApplyCapabilityOptions replaces the derived breathing-mode list; observing SupportsWaveDirectionSelection alone is not a completion barrier.

## Tried and ruled out
Hosted quality CI for PR #861 at87e01298 observed the new true capability flag and the old one-mode list (expected3, actual1). Keep every existing assertion, add no sleep, and do not reorder production notification or capability code. Use RecordingDispatcher.CompletedCalls captured before each publication, as other complete-presentation tests already do.

## Watch out
This is a test synchronization correction, not a change to lighting capabilities, model admission or hardware behavior. The focused capability-presentation pair passed locally; require the final PR head's full hosted lane before merging. Keep the failure and follow-up within PR #861.
