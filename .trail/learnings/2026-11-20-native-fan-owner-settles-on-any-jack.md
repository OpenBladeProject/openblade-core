---
title: Native fan owner settles on any confirmed Automatic
author: Jack
date: 2026-11-20
branch: fix/native-fan-noop-owner-selector
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - rust/crates/openblade-domain/src/fan_history.rs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - docs/windows-rust-native-fans.md
---

## Decided
- A native fan owner is discharged, with progress 51 and no setter, whenever a fresh 0D/82 pair shows Automatic fans, under either selector. Both the explicit no-op close (`FanPermit::confirm_fan_noop`) and owned Stop cleanup (`Reader::apply_fan` cleanup branch) do this. Managed's basis: `ExactFanControlOwnership.ObserveTopology` clears ownership on any confirmed Automatic, and `RestoreOwnedAutomaticFansCoreAsync` writes nothing when `OwnedManualFanHandbackBlockReason` reports "Fan control already uses firmware-managed Automatic mode."
- The selector is performance state, not the owned manual-fan obligation. The 51 row keeps the owner's recorded selector; the reply names the selector firmware reports ("OpenBlade's earlier fan change under X was released without a fan setter ...").
- Grammar unchanged. `fan-history-v3-foreign-selector-automatic.json` holds the producer's exact bytes; the host test asserts them, and the domain and managed readers consume them (Classify, AcquireRetiredOwner).

## Tried and ruled out
- Writing the owner's old selector back before closing: an intermediate write with no firmware reason (AGENTS.md Direct transitions).
- Applying the deferred preset at Stop: shutdown never applies saved settings.

## Watch out
- Still fenced on purpose: disagreeing or unclassified readback, manual fans under another selector (Stop's write path keeps the owner's selector), and a power-class change (`record.source != source`), including battery, where `Plan::select` has no plan. Managed would also release on battery or a class change; that is a separate gap.
- Stop is one-shot: a refused cleanup sets `release` and cannot be retried in-process.
- The vector test runs only with service-battery-policy-v1 (v3 contract bytes).
