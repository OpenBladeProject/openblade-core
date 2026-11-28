---
title: Battery history needs independent freshness and shared settled boundaries
author: Jack
date: 2026-11-14
branch: fix/windows-rust-battery-corrections
touches:
  - rust/crates/openblade-domain/src/battery_history.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - src/OpenBlade.Deployment/WindowsNativeBatteryHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/private_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
---

## Decided
Battery policy intent and public hardware history have different lifetimes. Keep one bounded timestamped public observation cache across requests, updated only by independent battery reads. Install current-user intent projection before awaiting the worker so cancellation or final authority loss cannot lose an already committed memory override. Expiry must clear only the battery field even when sibling thermal publication has a newer timestamp. Rebase a battery-only delta onto the latest publication; do not move a thermal read witness across the await. Classify recoverable versus fatal errors consistently across all private decorators and preserve existing diagnostics.

Stored generic limits50..99 are provenance, while exact0581 enabled destinations and newly supplied limits remain50..80/5. Off is its independent admitted command and preserves the saved limit. Never clamp old intent or treat cached temporary observations as admission.

History shape checks run before Option erasure: absent battery remains valid legacy history; present battery must be an object with explicit fan_owner, null when no fan is owned. Shared archive reserve, prefix inspection and recovery use settled progress including completed62/63, retaining strict rows and handback_complete so pending60/61 and owned30 remain fenced.

## Tried and ruled out
Clearing all historical observations on ambiguous output destroys legitimate last-confirmed presentation. Replacing a retained callback before a no-read refusal loses useful history; chaining old callbacks is unbounded and can retain private users. Caching whole decorated snapshots gives battery observations a newer thermal lifetime. A synthetic legacy tail isolates malformed-shape tests but cannot repair production battery-only rollover. Generic storage validation is not firmware authority; unsupported newly requested limits remain refused even while protection is Off.

## Watch out
The battery module/API currently compiles in shared fan/thermal feature compositions, so shared presentation fields must compile there too. Keep cached observations presentation-only, fresh exact-device/caller/controller/source/owner admission and no-replay fences unchanged. Test complete correctly hashed active and archived neighbors through actual parsers/proof consumers; a stale intent hash or missing private fixture root is not defect evidence. These are offline software corrections, not installed or physical validation.
