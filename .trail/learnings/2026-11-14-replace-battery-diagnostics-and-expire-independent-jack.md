---
title: Replace battery diagnostics and expire independent read fields
author: Jack
date: 2026-11-14
branch: fix/windows-rust-battery-publication
touches:
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/battery.rs
---

## Decided
Keep diagnostic provenance internal to Snapshot: public source/thermal reasons, a replaceable battery contribution, private policy wording, and Thermal/Battery degradation contributions. The v80 message remains one compatible string. Private redaction drops private-origin wording and retains explicitly public contributors. A recoverable callback starts by replacing only its own old failure; nested current failures and the other family's failure survive. A BatteryUpdate clears Battery degradation only after desired-state reconstruction succeeds. Accepted fan/performance rebases have reconstructed private profiles, so clear Thermal only, keeping the latest unrelated Battery marker rather than an older delta's array.

Keep one bounded public hardware history with independent policy and temporary-full-charge getter completion times. Record every independently valid policy result, including a third value while progress 61 stays unresolved. Cached observations never grant write authority; every request still needs its own fresh policy and TFC reads. The existing C# battery group requires a policy and rejects Off plus TFC true: policy expiry/conflict makes that public group unavailable; a live policy with expired TFC retains unknown TFC. Keep still-valid TFC internally without renewing it.

Use the existing active-subscription nearest-deadline scheduler for battery field expiry. It already has a two-second metadata cadence. One injected actual-subscription test reproduced retained battery publication after an unaligned five-second expiry against unchanged 59ccb0e2, without GetStatus driving expiry. Final injected delivery allows Tokio's one-millisecond timer granularity; real scheduling/execution latency is not established.

## Tried and ruled out
Appending/deduplicating English battery strings does not replace a diagnostic contribution. Restoring the rendered message after private redaction loses provenance. A single timestamp for policy and TFC makes one getter rejuvenate the other. Recording only successful requested-target readback discards a valid third observation. Exact nanosecond timer delivery is too strong for Tokio; the same one-tick subscription assertion is RED on unchanged production. The claims of absent polling/indefinite subscription staleness and nonpending warning at progress 61 were refuted.

Independent review found sticky degradation flags and a stale whole-array copy in queued thermal rebasing. Focused REDs protect callback recovery, immediate typed-delta recovery, and latest-versus-delta unrelated failure preservation.

## Watch out
Keep direct Off, exact0581 typed commands, the full reserved budget, one setter/nine confirmation getters with spacing, separate fresh TFC guard, original fan ownership/release IDs, strict history shapes, and pending/no-replay fences. Completed 62/63 remain distinct from pending 60/61 and owned fan 30. No new wire fields, numeric progress meanings, command/model or TFC setter. This stage is offline source evidence; installed managed defaults and physical qualification are unchanged. Reuse the existing narrow injected rigs rather than replaying a broad matrix.
