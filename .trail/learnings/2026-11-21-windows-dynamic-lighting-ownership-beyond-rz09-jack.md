---
title: Windows Dynamic Lighting ownership beyond RZ09-0528
author: Jack
date: 2026-11-21
branch: feat/dynamic-lighting-ownership
touches:
  - CHANGELOG.md
---

## Decided
- Windows Dynamic Lighting ownership is admitted only through `WindowsDynamicLightingLampArrays` (Contracts): RZ09-0528, 0529, 0509, 0510, 0530, each with its own pinned image descriptor offset. RZ09-0483 (SO690) has no LampArray; RZ09-0581 is unreviewed. PR #992.
- The Session Agent reports Owned/Released over `ReportWindowsDynamicLightingOwnershipRequest` (protocol 85, active local session). A failed, ambiguous or multi-array observation sends nothing; it never becomes Released.
- The service treats an observed Owned as delegation (`IsKeyboardLightingDelegated`), clears pending restore and suppressions, and never rewrites the saved preference. Release replays nothing.
- Renderer retirement: experimental ports use the write-free low-power close, because their normal close writes a restore frame. RZ09-0528 keeps the existing delegation close, because a low-power close drops its Driver-mode accounting.

## Tried and ruled out
- Relying on Revision alone in the tray: `MainViewModel.StatusMetadataEqual` (MainViewModel.Status.cs) short-circuits to live readings when metadata is equal. A new status field missing from it is never presented, and every view-model test for that field times out.

## Watch out
- Every new `ServiceStatusSnapshot` field needs four updates: `StatusMetadataEqual` in Tray.Core, `ServiceStatusSnapshotComparer` in Service, the protocol bump, and the wire-shape hash and public API baseline.
- Detection is not physically validated on any of the four new models; the `AmbientLightingEnabled` registry value and the WinRT `IsEnabled` semantics are assumed from 0528 work.
