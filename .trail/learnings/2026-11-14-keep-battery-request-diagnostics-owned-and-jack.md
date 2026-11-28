---
title: Keep battery request diagnostics owned and reconciliation neutral
author: Jack
date: 2026-11-14
branch: fix/windows-rust-battery-publication
touches:
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
---

## Decided
A public battery observation and a caller's request outcome need separate provenance even when rendered into one existing v80 message. Keep one bounded battery family with a public observation string and optional outcome carrying its original SID. Render an outcome only while that SID matches the private-data binding. Foreign/unbound/inactive redaction and neutral private reset discard the outcome; mismatched binding discards it permanently, and every binding re-renders. A later bind must not reown or revive the previous caller's text.

Direct BatteryUpdate carries public text chosen from the existing safe pending reason or a neutral historical-observation summary, separately from outcome.message. Public battery recomposition replaces the current family; desired-only projection preserves its current public provenance. Keep source/fan/thermal reasons and independent family reconciliation.

Distinguish neutral private reconstruction from actual caller filtering. Storage failure clears private fields and retains its family reconciliation without manufacturing caller denial. Actual foreign filtering still marks denial. A neutral reset preserves an already genuine denial until authoritative owner binding clears it. Shared thermal/fan reconstruction uses the neutral helper; accepted thermal rebasing goes through binding rather than copying a stale SID/denial flag. Binding resolves presentation only and does not clear another family's error or mint write authority.

The authorized a019a788 follow-up supersedes that unaccepted source while preserving its immutable objects and evidence. Six focused assertions are RED on unchanged a019 with test-only additions, then GREEN on the correction. The selected shared paths and unchanged strict native-frame C# facts pass; missing fixture input remains fail-closed.

## Tried and ruled out
One battery diagnostic slot holding both public observations and request-specific outcomes survives redaction incorrectly. A single redacted flag also conflates storage failure with caller denial. Suppressing text only until the next bind is insufficient: explicit ownership and permanent discard prevent later reclassification.

A broader SID private-field leak was not established. Native primary/active-console guards, retained fan custody and pre-mutation recomposition refute the simple simultaneous or sequential A/B recipe. Synthetic diagnostic rebinding tests do not establish unrelated production field disclosure and justify no new context restrictions. Examined outcome strings are mostly fixed; sensitive-value disclosure remains conditional.

The conditional fixture review follows existing required-input conventions; no skip, weakened codec assertion or CI redesign belongs in this correction. Full native fixture/CI orchestration remains a separate gate.

## Watch out
Keep typed fatal custody/origin failures fatal, private fields omitted when unavailable, writes blocked, per-family recovery, independent policy/TFC ages and existing expiry deadlines. No cached observation grants write admission. Preserve exact0581 commands, separate fresh TFC getter, complete budget, one setter/nine confirmation getters, original fan release IDs, strict history and pending/no-replay fences. No new command/model/TFC setter/IPC/default activation. Installed managed app and physical qualification remain unchanged. Keep prior a019 evidence qualified with its original identity.
