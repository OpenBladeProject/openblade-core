---
title: Preserve shared SnapTap profiles when merging driver API compatibility policy
author: Jack
date: 2026-10-20
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - docs/blade18-fn-decoder.md
  - docs/blade18-observer-evidence.md
  - docs/blade18-source-feature-recovery.md
  - docs/experimental-blade-support.md
  - docs/rz09-0528-support-migration.md
  - docs/snap-tap-driver-review.md
  - docs/snap-tap-filter-abi.md
---

## Decided
When main added shared SnapTap profiles for all seven supported models, retain every reviewed model's service, PID, topology, experimental provenance and shared A/D backend while replacing the new runtime hash fields with installed package/CAT trust and observed identity. The maintainer's AGENTS API policy applies to newly merged profiles too. Automatic driver installation stays RZ09-0581-only. Main is now 0.38.0, so this independent capability PR advances to 0.39.0.

## Tried and ruled out
Choosing the old 0581-only side of the merge would drop support; choosing the new seven-model side unchanged would restore forbidden runtime build allowlists. Reconcile both contracts. The four RESW catalogs are exact parent key/value unions with 747 keys each; preserve THX setup and new SnapTap status wording without retranslation. Source hashes in review documents remain provenance and installation artifacts remain exactly verified.

## Watch out
The 0255 catalog covers legacy INFs rather than parallel U variants. Discover the actual installed package instead of guessing its INF name. Only 0581 package layouts and Windows catalog binding were exercised locally; other models retain source-reviewed experimental status. The older Blade18 SnapTap-unavailable conclusion is superseded by the exact callback review; legacy THX fresh installation remains blocked on endpoint FX binding. No new model or hardware-write authority is added by this merge.
