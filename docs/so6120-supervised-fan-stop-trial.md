# Supervised SO6120 direct PWM stop trial

The successful Balanced half-cycle trial established direct PWM readback at
91/91 and verified Auto handback on the exact RZ09-0581. The next fixed
experiment tests whether that same path can report zero rotation after a
confirmed spinning baseline. It does not enable a production fan-stop switch.

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
