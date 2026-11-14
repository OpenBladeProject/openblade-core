---
title: Render identical owned battery pending wording once
author: Jack
date: 2026-11-14
branch: fix/windows-rust-battery-publication
touches:
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
---

## Decided
Render an identical complete battery public/request reason once for its authorized owner, after the existing SID/caller-denial check. Compare only those two already-classified battery contributions with exact whole-string equality. Keep request provenance and ownership unchanged, and keep the public warning for foreign, unbound and inactive views.

The stable pending-61 direct publication supplies the same complete pending/no-replay reason in public_message and outcome.message. A focused actual dispatcher case inspects that direct publication before GetStatus recomposition and requires one owner warning while retaining the pending fence, unrelated source reason and one-setter/two-read observations. A separate pure-render control preserves an intentionally identical unrelated source contribution, a distinct owner-only outcome and even a whitespace-different request.

## Tried and ruled out
The unchanged 8c6c368c owner publication rendered the pending reason twice. Its pure cross-family control rendered three copies instead of the expected two. Both occurrence-count assertions are genuine REDs on unchanged production plus test additions, then GREEN with the local renderer condition.

Do not turn this into global deduplication, substring stripping, normalization or private-to-public reclassification. The earlier prohibition on using English fragment matching instead of diagnostic provenance still applies. Exact equality here operates only after typed provenance and ownership are already established.

## Watch out
The pending reason remains public and the operation remains unresolved at61 with no replay. Different private outcomes must remain owner-only; redaction/rebinding still discards them without revival. Keep unrelated family errors, neutral storage reconciliation, independent policy/TFC ages, expiry, all command/admission/budget/ID/fan-owner/history/IPC constraints and installed managed defaults unchanged. Prior 8c/a019 evidence keeps its original identity; this is a small same-stage presentation correction, not a new functional phase or hardware validation.
