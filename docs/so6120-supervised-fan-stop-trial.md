# Supervised SO6120 direct PWM stop trial

The successful Balanced half-cycle trial established direct PWM readback at
91/91 and verified Auto handback on the exact RZ09-0581. The fixed stop
experiment subsequently confirmed zero PWM and consecutive zero-tach readings
after a confirmed spinning baseline. It does not enable a production fan-stop
switch.

## Stop/Auto control implementation

The tray now has a separate **Stop fans** control backed by a typed service
request. Turning it off requests Auto. The service owns the state machine and
serializes it with ordinary thermal operations. Saved profile fan settings are
kept separate from temporary Stop ownership. The UI waits for authoritative
service state and shows recovery uncertainty separately from those saved settings.

The [service guardian integration](so6120-stop-auto-guardian.md) supplies the
backend through a separately packaged native owner. Exact-device evidence and
backend availability determine `SupportsFanStop`; fresh entry admission remains
separate. The old Battery Saver HID/RPM stop path remains disabled. The control
does not run Capture or install a driver. An existing developer attempt marker
blocks acquisition until independently reconciled. Service IPC version 75 carries
the typed request and pending confirmation states; this requires a coordinated
product update rather than a tray-only deployment.

The shared controller has an injected backend contract for fresh identity,
controller, power, session, ownership and transaction admission. Stop also
requires the admitted entry scope and temperatures. Owned Auto cleanup is
independent of the Stop entry profile and temperature limits, while retaining
its own fresh safety admission. Ambiguity latches recovery and blocks further
commands; an ordinary mapped A0 read is not an independent recovery mechanism.

The injectable backend must supply a command-free startup ownership verdict
before ordinary service controls can run. Unknown or unresolved ownership
starts the controller in recovery. The service integration implements durable
startup inspection without replaying or adopting an old journal. The separate
developer guardian has
[attended caller-loss evidence](so6120-guardian-caller-loss-validation.md).
The new installed delegation path has not yet been physically tested.
Later observation of direct Stop without owned entry also requires recovery;
it neither establishes ownership nor authorizes an Auto command.

The [crash-recovery investigation](so6120-fan-stop-crash-recovery.md) describes
the surviving-owner design and its recovery limits. Simulated controller and UI
tests do not establish physical validation of the installed path.

## Attended result on 2026-11-06

Run `8b5cc38d-0864-4f0a-9ad7-1eb68b1254c7` used the reviewed candidate
`ae26bd6c9501448861672908297f8443534e8089` in USB-C Balanced. The captured
cycle was 183. Complete observations showed the following progression:

| Stage | Actual PWM | Raw tach |
|---|---|---|
| Captured Auto baseline | 51/43 | 22/19 |
| Confirmed half-cycle spin | 91/91 | 36/36 |
| First fan zeroed | 0/91 | 13/36, then 0/36 |
| Both zeroed | 0/0 | 0/12 |
| Two consecutive stop observations | 0/0 | 0/0 |
| Restored-controls checkpoint | 43/37 | 19/17 |
| Final Auto observation | 40/0 | 18/0 |

The zero observations retained FE, F0.6 and staging 0/0. Every A1 had an
independent A0 confirmation. All 1,821 chained journal records verified,
covering 437 A0 reads and eight A1 writes. Auto restored selector 00,
Manual00, F0=00 and saved staging 0/0; no redundant staging restoration was
needed. Native close and both process exits succeeded. Ten thermal samples
reported CPU thermal-zone 45 C and an inactive GPU.

The operator initially reported a stop, then withdrew that confirmation and
clarified that the zero-duty and Auto stages were difficult to distinguish.
Physical stop was therefore unconfirmed for this first run; its EC evidence
is retained separately. Auto may restart the fans before the console closes.

Execution lasted about 72.3 seconds, including 18.1 seconds in the stop stage
and 31.5 seconds for Auto handback. The two additional coast reads started
within the five-second window; the second completed after it. Possible F0
publication to confirmed restoration was about 54.2 seconds. These are host
measurement intervals, not exact physical stop or permission durations.

The operator explicitly requested one repeat of the same frozen candidate.
Run `a36a5107-4b0d-45d2-87c8-39f976118fd7` began at Auto PWM 47/42 with raw
tach 21/19. It again confirmed FE at 91/91 with raw tach 36/36, then actual
PWM 0/91 after B8=0 and 0/0 after B9=0. Two consecutive observations showed
actual PWM and raw tach 0/0 under FE. Auto independently restored every saved
control; the final Auto reading was 47/42 with raw tach 21/19. Native close
and both process exits succeeded. Execution lasted about 49.6 seconds,
including 14.4 seconds in the stop stage and 13.2 seconds for Auto.

After the repeat, the operator specifically confirmed: **Both fans briefly
stopped.** This observation supports physical stop on this exact laptop in
USB-C Balanced alongside the two runs' EC evidence. It does not establish a
calibrated RPM scale, exact stop duration, Silent behavior, another model's
support or unattended recovery. The repeat changed no runner code, commands
or limits, and no production fan-stop control has been enabled.

## Fixed sequence

`validate-so6120-balanced-pwm-stop --plan` is offline. The attended command
uses the same exact host, firmware provenance, driver, controller, power,
thermal, ownership, console and one-shot admission as the
[supervised PWM runner](so6120-supervised-pwm-trial.md). Keep the captured
USB-C Balanced/Automatic profile; no preparatory profile or RPM command is
needed. USB-C is operator-reported context, while Windows confirms external
power.

The approved plan fixes both initial targets at half the captured cycle,
rounded down, and fixes the following targets at zero. Cycle 183 yields
`91/91 -> 0/0 -> Auto`. The new policy is included in the plan digest. Old
strict +4, bounded +4 and half-cycle approvals keep their previous meanings.
There is no raw target argument, silent rebasing or automatic repeat.

The runner first stages the half-cycle pair and enters FE through the already
exercised sequence. Before the first zero write, a fresh complete observation
must confirm the approved PWM on both channels, nonzero raw tach readings,
FE with F0.6, and the expected manual, cycle, policy and active-state fields.
An already-zero baseline alone cannot establish that the zero command stopped
a spinning fan.

A durable, one-shot stop claim then admits only B8=0 followed by B9=0. Each
write retains fresh admission, separate A0 confirmation and its complete
mapped-state checkpoint. The pair is sequential, so at cycle 183 a temporary
mixed staged pair `0/91` is expected. The actual PWM updates on the firmware
schedule. FE is not republished. The second write must still match
its confirmed prefix and fan 2's approved running state; fan 1 must be at or
below its previously confirmed half-cycle PWM.

After both staging writes, the runner observes actual PWM and raw tach through
bounded ordered snapshots. Two consecutive observations with actual PWM and
raw tach both `0/0` provide EC-indicated zero-rotation evidence. A five-second
window bounds starting new coast observations, with at most four new complete
samples beyond the post-write checkpoint. Confirmation and handback add time;
this is not a five-second maximum physical exposure or a hard watchdog.

## What zero means in the recovered firmware

The pinned EC 1.04 image maps B8/B9 to RAM `80394/80395`. The A1 handler at
`2388–2394` stores the payload byte unchanged, including zero. With active
state, selector FE and F0.6 confirmed, the recurring fan routine reads those
requests and stores them directly to PWM MMIO at `1628A/162BA`. This branch
has no zero sentinel, minimum clamp or conversion. Active target changes do
not need a second FE command, a policy command, or a paired HID write.

B0/B1 independently expose the PWM MMIO bytes. Staging readback alone is not
proof that zero PWM was applied. Raw tach fields 08/09 come from the EC's
collector at `149D4`, which can retain earlier samples across missed updates
and quantizes its internal estimates. Thus zero PWM, zero tach, and an
attended observation of the fans are distinct evidence. The trial records the samples without asserting an
RPM scale or independent mechanical proof from a raw zero.

## Restoration and failure handling

The existing generic Auto exit restores the captured normal selector first,
confirms Manual00, then restores F0 and any staging bytes that differ from
the saved state. It does not return to 91 before Auto. If a confirmed zero
already equals a saved staging byte, the durable checkpoint resolves that
field's restoration responsibility without a redundant write. Saved nonzero
staging is restored only after leaving FE.

A clean timeout, no zero-tach result, cancellation, or a definitely unsubmitted
second zero write still uses the latest confirmed prefix for Auto handback.
Unknown or high temperature prevents further experimental writes but does not
block otherwise admitted Auto restoration. An ambiguous publication/readback,
unexpected controls or unresolved storage state stops all mailbox commands;
there is no guessed cleanup or retry.

The existing 45-second entry-work and 30-second permission-work cutoffs remain.
An already-started transaction and its mandatory confirmation/checkpoint finish
before a resolved stop. Auto retains its separate admission and phase budget.
Synchronous native I/O or storage can still outlast software deadlines.

Report the spinning baseline, applied zero PWM, zero-tach observations, Auto
restoration and native/process closure separately. A completed journal proves
cleanup, even when the stop observation itself was unsuccessful. Physical
execution requires approval of a frozen reviewed candidate and this complete
sequence; prior half-cycle approval has been consumed.
