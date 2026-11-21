---
title: Native migration re-checks its prior and clears holds on success
author: Jack
date: 2026-11-21
branch: feat/native-profile-schema-migration
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - docs/windows-native-thermal-mutation.md
---

This follows 2026-11-21-native-migration-at-open-keeps-the-jack.md after the final review. It supersedes that learning's hold-clearing rule and corrects its cap claim.

## Decided
- `Planned::write` reads the three documents again before the first backup and returns `MIGRATION_UNCHANGED` with no write when they differ from the planned prior. The plan and the write are separated by override and journal admission. Without the check, a second writer's newer aggregate was backed up as nothing and the transaction's restore wrote the planned prior over it.
- The mismatch hold is cleared by `profiles::opened(user_sid)`, called at the end of a successful `Storage::open_with`, when the SID differs. `held()` is read-only. Managed reloads the held user only after another user activates successfully (`ServiceControlRuntime.cs:1456-1487`), so another user's refused open leaves the hold and another user's successful open of a current store clears it.

## Tried and ruled out
- Clearing the hold inside `held()` at the other user's plan. That was too narrow, because a current store never plans, and too wide, because a plan followed by a refused open still cleared.

## Watch out
- Correction to the earlier learning: the old 16-entry cap held 16 stores and failed only at the 17th. It did not hold nothing.
- A change between the re-check and the transaction is still handled as a save's transaction handles it. The re-check narrows the window; it does not close it.
