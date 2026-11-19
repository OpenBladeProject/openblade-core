---
title: native display profile apply composition
author: Jack
date: 2026-11-19
branch: feat/native-agent-display-profile
---

## Decided
- Native applyProfile (`agent-display-profile-apply-v1`, marker `session-display-profile-apply-partial-v1`) composes the four reviewed single-field attempts in `profile_apply.rs`; it adds no setter. The feature implies all four write features, and both admission layers (Rust `native_package::capabilities`, C# `WindowsNativePackage.RequireCapabilities`) refuse the profile without every write.
- One checkpoint covers every configured field; the gate refuses the whole profile if any configured field is unresolved. Steps follow managed `ApplyProfileCoreAsync` order and skip conditions on the raw snapshot (`Snapshot::observed`), and the first non-success ends the profile.
- Durable record keeps managed semantics (Indeterminate keeps every configured field uncertain). The native gate gets exactly: possibly submitted unconfirmed steps, stale submitted steps, and confirmed steps the final reply does not show (an unavailable final reply shows none). `Recovery::complete_with` takes that set.
- Fatal query or brightness worker errors are recorded before returning Err, as the managed dispatcher records an exception: Indeterminate once the profile selects steps, Succeeded for an unconfigured profile and Blocked for a power-refused one (nothing submitted).

## Tried and ruled out
- Counting a no-op setter as a submission (managed does): native counts only real or possible submissions, so a later pre-submission failure is Failed/Blocked.
- Continuing after a confirmed step whose reply does not show it (managed does): native stops there. A 14688-case differential against the managed applier on a static display differs only in that case (same status, native writes a prefix). That harness only: with displays that change between steps, native also decides later fields from its fresh reply query rather than the setter's result snapshot.

## Watch out
- `WireSettings` requires every member the managed codec writes, including the computed `isConfigured`. Managed requires `refreshRateKey`, `hdrEnabled` and `colorProfileKey` too; it accepts a missing `brightnessPercent` or `isConfigured`, PascalCase and string percents (printed by the oracle as observations, not fixture rows).
- After an Agent restart the gate reloads the durable uncertain fields, so an Indeterminate profile gates all of its fields until an explicit read.
- The request fixture now has 71 rows; `request_parity_tests` pins the count.
