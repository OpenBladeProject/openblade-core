---
title: Qualify transient names and budgets before mailbox challenges
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - tools/mailbox-observation
  - docs/so6120-mailbox-calibration.md
  - docs/so6120-mailbox-trigger-test-plan.md
---

## Decided

The first attended mailbox Calibration run cleanly stopped both owned sessions with zero reported loss. Offline analysis correctly stays inconclusive: held-file rundown matched, while the controlled transient create/close pair had blank names. Exact PID, handle, File type, object and narrow timing correlated its lifetime; that is distinct from satisfying the named-close control. Preserve that distinction instead of calling the event missing or silently weakening the control.

## Tried and ruled out

Calibration-only returns captured before the action-volume gate. A successful five-second recording therefore does not imply that the configured 50-60 second action windows fit. Both measured live-byte projections exceed their watermarks in this capture. An ETL-only cap increase would leave the offline event/output limits unresolved. Blank names are not a decoder schema failure here; normal File-close records and the held-file name decode successfully.

## Watch out

Treat live-byte extrapolation as a conservative budget gate, not an exact steady-state rate: ETL padding and startup costs are included. Qualify the transient named control and revise collection/export limits together before the next physical-action batch. Keep residual-risk acceptance finite; this calibration establishes neither a competing client nor a clean action challenge. No extra recording, firmware method, device call or fan/profile change follows from this result. Sanitized evidence is in docs/so6120-mailbox-calibration.md; raw ETLs and identities stay out of Git.
