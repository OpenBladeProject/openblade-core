# SO6120 guardian attempts before caller-loss validation

These earlier refusals were followed by a
[successful attended caller-loss test](so6120-guardian-caller-loss-validation.md).
The historical evidence and outcomes below remain unchanged.

The attended USB-C Balanced attempt on 2026-11-07 used Capture commit
`1ceb0112d03f24f0fb1661df266fc691ba9bac0d` on the already admitted RZ09-0581.
The guardian passed its caller/private-job and console admission while an
ambient Windows job remained present. This exercises the corrected startup
path; it does not establish survival of caller loss or an outer job termination.

The baseline refused the fixed half-cycle trial before APPLY:

| Captured field | Value |
| --- | --- |
| Profile / fan control | Balanced / Automatic |
| PWM cycle | 183 |
| Fixed target | 91 / 91 |
| Actual PWM | 97 / 90 |
| Raw tach | 38 / 36 |
| CPU thermal zone / GPU | 45 C / confirmed inactive |
| Selector, manual, override, staging, policy command | All zero |

The first fan was already above its target. The existing preparation gate
requires each target to exceed that fan's current PWM. No trial policy, target,
or hardware gate was changed to bypass this refusal.

Offline reconciliation verified the entire 55-frame SHA-256 journal chain with
no partial tail: one binding, one preparation claim, thirteen admission/intent/
publication/result groups, and one complete observation. All thirteen commands
were completed A0 reads, every write value was null, and no A1 setting command
was published. There was no prepared plan, APPLY consent, entry, zero checkpoint,
caller termination, or Auto restoration command. Automatic was observed and
left unchanged by the trial.

The native handle closed with state Closed and native error zero, with no
retained ownership or release failures. The controller observed the original
caller and guardian exit, and the parent observed the controller exit. The
generic completion remains incomplete, and its conservative
`knownNoSettingWrite=false` and `cleanupConfirmed=false` fields were preserved.
They are not successful restoration receipts. The no-setting-write conclusion
comes from the complete offline journal and the source path before preparation
returns. No hardware commands were sent during that reconciliation.

At the end of that attempt, its marker and all original artifacts remained
intact. A further trial needs reviewed marker disposition, a fresh attended
baseline with both PWM values below the fixed target, and the existing runtime
admission. Caller-loss Auto recovery remains physically unvalidated. Do not
widen the target or replay this attempt to obtain a passing result.

## Local evidence pins

Run: `551b0f10-b731-45b4-bbd7-8a838338e605`.
Ignored artifact directory: `artifacts/so6120-guardian-caller-job-trial-20261107`.

| File | SHA-256 |
| --- | --- |
| `attended-attempt/attended-run.jsonl` | `AD05DAEE28FBFADAA4F23BAADAEE3FC6A1EF5182D859601000810C4AD7DB75AC` |
| `attended-attempt/so6120-direct-duty.journal` | `113245F25960E45309707709F89433875BAF38E9513654EABA5C2E5B23D4F230` |
| `capture-result.json` | `F0CDCAAEE917B14431A3BE79DC15CD8D25168FF14C7BEBA66424A3795668BC94` |
| `parent-result.json` | `F7194A9F6530D0519EFB30F00D1B52DC4B2CCAC67EB7DCFAF2378A62F5F63347` |
| `attended-attempt.guardian-startup.json` | `147CDB0BC67AAFDF011C1399614C04ABBCE76258CFAC49B97BAB9B4FC57D2C15` |

The startup record's `acquisitionStartedAtRecord=false` describes its recording
instant only. This attempt subsequently acquired hardware for the baseline.
It must not be classified as another pre-acquisition isolation refusal.

## Continuation refused before acquisition

The authorized continuation at 08:47 UTC on 2026-11-07 preserved the original
evidence and archived only the audited no-setting-write marker under the shared
semaphore. Its run ID and UTC window were verified, the move did not overwrite
another file, and the archived bytes were independently hashed. The archive is
`attempt-551b0f10-b731-45b4-bbd7-8a838338e605-no-setting-write.json`, SHA-256
`E9E0EA958B9C6CB074C72E04E3C259CFAFCF9DD79DC9AE3F7B0F9175F51B7DC3`.

New run `0b94a231-787b-4272-a8e8-f6072e2173ed` passed guardian startup and accepted
BASELINE, then refused the thermal precheck: CPU 45 C, GPU 64 C, GPU active.
The unchanged trial requires an inactive GPU for entry. The run never acquired
InpOut, sent no mailbox commands, and consumed no new machine attempt latch.
There is no direct-duty journal. The controller observed both caller and owner
exit with code 2, and its parent observed controller exit with code 2. No
caller termination, fan-setting write or Auto restoration was exercised.

The original payload and fixed targets remained unchanged. Further execution
waited for the user to finish the GPU workload and for fresh runtime admission;
the preparation work did not close applications or relax the thermal gate.

Evidence directory: `artifacts/so6120-guardian-caller-job-repeat-20261107`.

| File | SHA-256 |
| --- | --- |
| `attended-attempt/attended-run.jsonl` | `8A731E674C2E2C63F92D4EAC16D4CDFA1BBC414E697C8A5A334A12A6F672290F` |
| `capture-result.json` | `C36510565DC2CEC6C852EB37DD02F50FA8ABCBDCBFE97ED32FA61789B767423F` |
| `parent-result.json` | `B80D234C510327B9137F22CEFF5DBD2E4A3AD2211556D0D7C6276900E02C14F7` |
| `archive-result.json` | `6814AF263C88BC1FC407EA80AF295B3736922DE1BBF3A6EE9D9EE95E646BE129` |
| `attended-attempt.guardian-startup.json` | `DF1A8EEB8478A14BD9DA895F9690A6513A78B80ECE6B06728FDE36043327E831` |
