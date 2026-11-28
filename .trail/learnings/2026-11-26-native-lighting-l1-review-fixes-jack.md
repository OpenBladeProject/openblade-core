---
title: Native lighting L1 review fixes
author: Jack
date: 2026-11-26
branch: feat/native-lighting
---

## Decided
- Lighting power is classified inside the job (07/8C plus Windows), and an unclassified adapter admits lighting only under managed CanAdmitUnclassifiedExternalControl, each clause from fresh native evidence.
- Status and the convergence ledger write lighting records kind first through `openblade_domain::kind_first` (`FeatureRecord` in the host).

## Tried and ruled out
- Putting `keyboardLightingControlMode` at the top of `battery-preferences.json` in a test: the members live under `preferences`, and with triggers `Storage::open` refuses the aggregate ("Uncommitted or non-current profile aggregate").

## Watch out
- Every compiled-identity test must strip the markers build.rs appends after its own (lighting is last); the live-readings one only ran in a lane CI did not filter for until `the_compiled_identity` was added to the live lane.
- A test that holds a lighting setter (`lighting_held`) must be counted in `parks()`, or the paused clock never advances and the test binary hangs and locks the linker (LNK1104).
