---
title: Driver keyboard stop releases delivered keys before a clean exit
author: Jack
date: 2026-11-25
---

## Decided
- All driver keyboard output goes through `DriverKeyboardOutput`. It decides from one snapshot (`SnapTapDriverLoop.OutputDecision`: TargetChanged > StopRequested > Allowed) and records the scan codes Windows holds down only after `submit` returns.
- A stop-caused refusal (A/D batch index 0, or a function-row stroke) with no other failure (`StopRefusalExit.MayRelease`) releases every delivered key under `stopping`, then exits 0/Off, so IsCleanStall rearms. This supersedes the 2026-11-25 note that stop-refused output stays a failure. That note covered the reverted 8cdc1d11, which released nothing.
- Any otherwise clean ordinary stop also releases delivered keys. That closed a pre-existing exit 0 with D down, where the function-row drain passed A/D through a disabled processor.
- The function-row processor is unchanged: our thrown deferral still leaves OutputUncertain=true. Only DriverKeyboardOutput knows the refusal was pre-submission.

## Tried and ruled out
- Treating the function-row KeyboardSubmissionDeferredException as not submitted inside the processor. That reclassified non-stop refusals and let exit 0 delete the ownership journal.

## Watch out
- A held suppressed media key at a stop refusal keeps RecoveryRequired (`FunctionKeysSettled`), even if its up is queued unread.
- The release costs a full admission per key; under heavy load the supervisor's 6 s kill can still win (fail-closed).
- Reviewer harness that mirrors Run() cleanup with the real classes: about 4.6M simulated runs, 0 violations. It doesn't cover driver behaviour (typematic after release, GetAsyncKeyState on injected keys).
