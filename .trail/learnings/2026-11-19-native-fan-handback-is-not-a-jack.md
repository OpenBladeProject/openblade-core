---
title: Native fan handback is not a manual-fan deferral
author: Jack
date: 2026-11-19
branch: fix/native-branch-red-tests
touches:
  - docs/windows-native-thermal-mutation.md
---

## Decided
- Corrects 2026-11-19-native-stored-replies-follow-managed-ipc79: native Stored is only the PluggedIn current-manual plus desired-manual deferral (`defer_manual_performance`). An outstanding OpenBlade-owned fan handback (`outstanding_fan()`, progress 30 without 50/51) is its own cause, `FanHandbackPending`, and replies Blocked with "Performance override intent was stored. Hardware result: ...". After a fan clear no manual intent remains, so the old Stored reply told the user to select Automatic fans for nothing.
- The deferral parity is reply-only. Managed applies the deferred intent when fans return to Automatic on AC; native does not. That missing apply-on-handback is a functional gap and a cutover gate, recorded in docs/windows-native-thermal-mutation.md.
- Native never replies Failed. Each managed Failed case (indeterminate, rejected write, read failure, changed final readback, rejected intent) shares its native path with a case managed reports Blocked: unsettled journal after this request also follows a checkpoint stop; lease errors mix transport and ownership; worker read failures return None without a class; the save refusal mixes quarantine with file errors. Left Blocked and listed in the doc.
- An ineligible request whose policy-selected preset is confirmed now says the policy selects another confirmed destination, not an admission failure.
- The reporter pipe is v80; no native path speaks v79 except the frozen rejection fixtures.

## Tried and ruled out
- Reachability walk over `metadata.resolve` in Check-Workspace.ps1: unnecessary. Default-feature `cargo metadata` lists only activated packages, so the sha2 closure only appears when a manifest enables thermal-history. Checking local features before external names makes those guard cases fail as feature errors, and the eight closure names are gone from the allowlist.

## Watch out
- `fan_rig(..)` with `actual = 10` is a non-AC source: an owned fan there is Blocked, not the AC deferral.
- rust.yml now runs `openblade-domain --features thermal-history` tests and clippy; the workspace build never enables that feature.
