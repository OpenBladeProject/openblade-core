# SO6120 attended caller-loss recovery

The supervised guardian test passed on 2026-11-07 at 02:24 PST
(10:24 UTC). After the hardware-free caller was deliberately terminated at a
confirmed zero-PWM checkpoint, the same guardian survived, restored Automatic,
verified its original owned fields, completed the journal and released native
ownership. Both fans had nonzero PWM and tach in the final Auto observation.

This result covers exact RZ09-0581 / `1532:02E0`, SO6120 EC 1.04, reported BIOS
4.01, USB-C Balanced, and frozen Capture commit
`1ceb0112d03f24f0fb1661df266fc691ba9bac0d`. These firmware versions identify this
developer test; they do not establish a production version allowlist.
The installed Stop backend remains unavailable.

## Observed sequence

The guardian passed sole-console and caller/private-job isolation. Ambient
Windows job membership remained present. The operator completed BASELINE and
APPLY in the guardian console. CPU thermal-zone readings were 45 C and the GPU
was confirmed inactive at entry. The reviewed controller retained the original
caller process handle and terminated only that process after two checks of the
fresh, complete zero checkpoint. It neither terminated the guardian nor
extended the trial's coast interval.

| Stage | Actual PWM | Raw tach | Controls |
| --- | --- | --- | --- |
| Original Automatic baseline | 56 / 52 | 25 / 23 | Normal selector, manual off, no override; staging zero |
| Confirmed spinning half-cycle | 91 / 91 | 36 / 36 | FE, F0.6 enabled, staging 91 / 91 |
| Zero checkpoint before caller exit | 0 / 0 | 0 / 16 | FE, F0.6 enabled, staging zero |
| Final Automatic observation | 40 / 40 | 18 / 18 | Original selector, manual, override, staging and policy command all zero |

The PWM cycle remained 183. These are raw EC values, not calibrated RPM or
electrical duty measurements. Caller loss intentionally interrupted coast
observation; this run does not establish that both impellers stopped.

The retained caller handle confirmed process exit before Auto began. At a QPC
frequency of 10,000,000 Hz, the recorded exit observation was `3693430696924`,
Auto began at `3693431193652` and finished at `3693542194063`: about 49.7 ms
between the exit observation and Auto start, then 11.10 s for the complete
handback. These are measured events from this run, not maximum timing promises.

The guardian reported CallerDisconnected, AutoConfirmed, CleanupConfirmed,
JournalCompleted, ReleaseCompleted and Resolved. Commands were not fenced.
Native close reported Closed with error zero, no retained ownership and no
release failures. The controller observed both original processes exit; its
parent then observed controller exit with code zero.

The underlying trial's `Success=false`, cancellation flag and owner exit code 2
are expected for this deliberate interruption. The separate caller-loss verdict
was true and the restoration result succeeded. Preserve those distinct outcomes
rather than converting cancellation into an ordinary completed stop trial.

Independent offline review verified the original journal's SHA-256 chain and
complete tail: 1,715 frames, 419 matched requests, 411 completed A0 reads and
eight completed A1 writes. Each A1 had an independent same-register A0 readback
and a complete snapshot/checkpoint before the next write. The write sequence
was B8=91, B9=91, F0=64, selector=FE, B8=0, B9=0, selector=0, F0=0. The final
Complete frame closed generation 9 at observation 31 with baseline-equal
controls. There were no native errors, exceptions or command fences.

## Scope and remaining work

This validates caller-process loss during a resolved, confirmed zero checkpoint
while the original guardian and hardware session survive. It does not validate
loss of the guardian, termination of an unknown outer job, hung native I/O,
sleep/resume, OS failure, or takeover by a restarted process. It does not admit
another model, Silent direct duty, unlimited Stop duration or an ordinary
unattended Stop switch. The existing source and exact-device admission rules
remain in force.

The completed machine attempt marker and original captures remain retained.
No further EC command was issued after the verified Auto handback. Installed-service
delegation and startup ownership reconciliation remain separate integration
work; this developer test does not enable the production backend.

## Evidence pins

Run: `bc53359d-f19a-4de7-bab6-1792a637d1e3`.
Ignored local directory: `artifacts/so6120-guardian-caller-job-idle-20261107`.

| File | SHA-256 |
| --- | --- |
| `Invoke-PreparedGuardianCallerJobIdle.ps1` | `C14E7C5B6CDAD86FAD3A1F4CF06955A5EA171466D060C01422F0D22C775A6F37` |
| `candidate-manifest.json` | `804D445AA9993C6EE651C09CA0D423DFDC7B94A61983A518E8D10021B3D1401D` |
| `attended-attempt/attended-run.jsonl` | `14E2AA891E2BFA3A18B7B2B2C059F1CBA47EA91122727CC921892672B8BBC9F9` |
| `attended-attempt/so6120-direct-duty.journal` | `ACA7B486BED5AB9719785EDB1B6D4C6CDE23F11501E618C51C707F55723D1D5D` |
| `capture-result.json` | `C8BAF41FA34233F421F23B1252149FBBD0C5A89EA7D59579A31F3A8E848C5597` |
| `parent-result.json` | `8DF2C7BFE1670540EB704D0FF7A10966729D44AEC7829392C45C2DD6809F3B0E` |
| `caller-exit-result.json` | `9C3F5357F74578CB07D016853AA53BED9527751B04A66C77AC4DAE0A3A829E6B` |
| `caller-exit-intent.json` | `C409309600D40A8879C624DD5FEB9613D2318A5D529042909DE62B23BCFF5C3A` |
| `attended-attempt.guardian-startup.json` | `7DF954E914F3D4472B9C494DA4BF216658A37C668402FE3B727FA988F4E08592` |
