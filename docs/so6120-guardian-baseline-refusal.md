# SO6120 guardian startup passed; caller-loss test pending

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

The attempt marker and all original artifacts remain intact. A further trial
needs reviewed marker disposition, a fresh attended
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
