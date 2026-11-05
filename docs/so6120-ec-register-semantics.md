# SO6120 fan-register semantics

The register follow-up on 2026-11-05 establishes that **zero is an ordinary
value at the PWM-register layer**: normal fan control writes zero to the same
registers used by internal direct duty. This differs from a zero-RPM request,
which still passes through the normal Silent minimum clamp.

The register path has several independent controls and status layers. The
override byte is not a single fan-off flag, and clearing its duty permission
does not restore an exact prior fan state. This report refines the
[bridge analysis](so6120-mcu107-ec-fan-bridge.md) and
[protection review](so6120-direct-duty-safety.md); it does not establish a
hardware test sequence or a production capability.

All findings use the same exact RZ09-0581 EC 1.04 and MCU 1.07 images pinned in
those reports. No hardware access, vendor execution, installed-state change,
or live fan comparison occurred. Other models are not inferred from this map.

The [offline firmware inspector](../tools/firmware/README.md) reproduces the
bank map and factory tables from those images and presents the reviewed
annotations in a searchable HTML explorer. It performs no hardware access.

## Duty requests, actual PWM and zero

| Bank-0 field | Backing location | Meaning and access |
|---|---|---|
| `B8/B9` | RAM `0x80394/0x80395` | Writable raw duty requests; copied by the permitted `FE` branch. |
| `B0/B1` | MMIO `0xF01807/0xF01803` | Actual PWM registers; the generic bank map exposes them read-only. |
| `F0` | RAM `0x80425` | Writable override flags; bit 6 permits `FE/FF`, with other effects below. |
| `1A` | RAM `0x8035F` | Mode selector, separate from permission and duty requests. |
| `0F` | RAM `0x8144C` | Shared bank selector, present at the same index in all eight banks. |
| `B2/B3` | MMIO `0xF01841` | Two read-only aliases of the shared PWM cycle byte. |
| `E7` | RAM `0x802AA` | Read-only low byte; bit 0 selects the normal-mode fallback source. |

Normal control `0x15510` writes zero at `0x1568C/0x15838` when target and
measured speed are zero. Its deceleration paths also write zero below their
per-fan duty floor; lifecycle routines `0x1488A/0x14948` clear both registers.
Some equal-zero paths can subsequently restore a remembered duty byte, so
these stores establish ordinary zero semantics rather than indefinitely held
zero output.
The `FE` copies at `0x1628A/0x162BA` use the requested bytes directly, with no
zero special case, percentage conversion or range check in that branch.

Initialization `0x14758` sets up the two PWM channels and writes `0xB7` (183)
to cycle register `0xF01841`; normal control uses that register as its duty
ceiling. Floor helper `0x166A6` computes integer `percent * cycle / 100`.
These are raw counts, not RPM or established physical duty percentages. The
byte-sized request does not establish that every value through 255 is valid.
The running cycle configuration was not read. Neither register zero nor an
accepted request alone proves a mechanically stopped fan.

## The override byte uses every bit

The following are instruction-backed consumers of `0x80425`. Peripheral
identities and physical units are left unnamed where the firmware trace does
not establish them. This is a bounded recovered-code inventory.

| Bit / mask | Effect when set | Evidence |
|---|---|---|
| 0 / `01` | Controls three other PWM channels from bits in `0x80426`, bypassing their normal branch. These are not the two fan duty registers. | `0xC50E..0xC55C` |
| 1 / `02` | Paired input/state handlers update `0x80427` bit `10` and return instead of their normal paths. | `0xA5F0..0xA606`, `0xA68C..0xA6A2` |
| 2 / `04` | Another handler pair updates `0x80427` bit `08`, bypassing normal state changes and an event. | `0xA6BC..0xA6D6`, `0xA712..0xA728` |
| 3 / `08` | Under surrounding gates, sets bit 1 in peripheral-policy word `0x80246`; the unset case clears it. | `0x10752..0x1076A` |
| 4 / `10` | Under the same gates, forces peripheral-policy word `0x8023C` to zero. | `0x10772..0x1077E` |
| 5 / `20` | Suppresses several sensor acquisition/derivation updates; some paths continue copying previous readings. | `0xFE64..0xFE90`, `0x12BE2..0x12BEA`, `0x1399A..0x139E4` |
| 6 / `40` | Permits special fan selectors `FE/FF` and independently holds policy/event contributor `0x80493`. | `0x161E6..0x161EE`, `0x16262..0x1626A`, `0x1101A..0x11022` |
| 7 / `80` | Couples two computed policy candidates before further limits and peripheral submissions. | `0x11F5E..0x11F68` |

Mapped writes replace the whole byte; no masked-bit or compare-and-swap
operation is provided by that store. Direct helper `0x12D08` clears this byte
and related override state during reviewed power transitions. Preserving the
other bits matters, but a host read-modify-write alone is not an atomic
ownership contract with the EC.

Bit 6 holds only one contributor. Routine `0x10FD4` still takes the maximum
of `0x80490..0x80495` into `0x8033C`; other contributors can change or dominate.
The resulting `D1..D5` values can pass through `0x1157A -> 0xFE3E -> 0x9DD0`
into the EC event ring at `0x803D0`. Host query `84` dequeues an event through
`0x9822/0x1FBEA` and outputs it at `0xF01501`. The downstream OS/ACPI meaning
and acknowledgment remain unknown. No specific wattage or actual performance
change is established. Clearing bit 6 permits later recomputation under the
routine's gates; it does not purge already queued events.

## Permission and mode are separate

| Mode and permission | Established behavior |
|---|---|
| Normal mode, bit 6 clear | Normal fan policy and normal contributor calculation, subject to their other gates. |
| Normal mode, bit 6 set | Normal fan policy still runs; the policy/event contributor is held. This alone does not select direct duty. |
| `FE`, bit 6 set | Raw duty bytes are copied; ordinary fan adjustment is skipped. |
| `FE`, bit 6 clear | Ordinary adjustment remains skipped while the selector is `FE`; the direct-duty routine selects a normal fallback. |

The `FE` fallback at `0x162CA..0x162DC` is now resolved:

- If `0x802AA` bit 0 is set, helper `0x1170E` reads saved mode `0x86062`.
  Modes 0, 1, 2, 4, 5 and 7 survive; 3, 6 and values above 7 become 2.
  It returns Silent 5 only if the saved byte is currently 5.
- Otherwise, helper `0x123FC` reads `0x86063` and returns 3 if that byte is 3,
  or 6 otherwise. That branch cannot return Silent 5.

The branch stores the chosen mode and clears stage bookkeeping. It does not
immediately write PWM, restore captured duty, or clear ordinary manual flag
`0x8034D`. Returning to mode 5 therefore does not mean returning to Automatic.
Saved normal modes, current manual state and subsequent scheduling matter.

An [explicit normal-mode handback](so6120-direct-pwm-handback.md) is a separate
route: an applied Silent selector plus confirmed Automatic flag can leave `FE`
without relying on the permission-clear fallback or visiting another profile.
It leaves the override byte and staged duty in place, and a cached UI selection
does not establish that the normal command reached the EC.

## A register response has two success checks

Unlike normal thermal GETs, the generic MCU read invokes an actual EC bus
transfer. However, the response can report outer status 2 while carrying a
bus failure in a separate byte:

- Class dispatcher `0x9598` initializes outer status to 1 and converts an
  unchanged status to 2 after the handler returns.
- Generic helpers return bus status into report offset `0x0C` at `0x7474`.
  They do not translate a bus error into outer failure. A closed access gate
  instead sets outer status 5.
- Read helper `0x5864` copies returned bytes only for bus status `0C`.
  On failure, its data area remains unchanged, so request contents can remain
  in a response that has outer status 2. Transfer timeout uses bus status `0B`.

Even a completed write does not prove a register changed: the EC skips
mapped read-only entries such as actual PWM while the bus can complete.
Actual register readback and tachometer observations remain distinct evidence.

## Bank and transaction boundaries

The reviewed eight-bank table has no alternate aliases for the duty request,
permission or actual PWM fields. The actual PWM entries cannot be used as a
second mapped write route. Bank selector `0F` is shared across all banks.

The SMBus handler loads that shared bank without a local `<=7` check and
adds starting index to transfer progress without masking the sum to eight
bits (`0x2512..0x2520`, `0x2590..0x25B6`). The separate BRAM interface's bank
bounds and wrapping behavior must not be assumed here. After a received
write byte, the handler selects the table again; a bank change can affect
later bytes of the same transfer. Bulk access is not established as safe.

Both MCU helpers overwrite shared transfer counts and buffer before starting
I2C. They do not first wait for an idle shared status; `0x2520` resets progress
and assigns busy status unconditionally. Normal main-loop work and generic
timer-dispatched commands use this same storage. Full interrupt/reentrancy
behavior remains unresolved, so this establishes neither an observed collision
nor a guaranteed exclusive interval for a host operation.

The concrete result is a more precise register contract: ordinary zero at the
PWM layer, separate requests/mode/override flags, conditional fallback, nested
status and shared bank/transfer state. It does not supply independent cooling
recovery or exact restoration. The next register-side uncertainty is how to
obtain exclusive access and reconcile MCU pending work, not another ordinary
zero-RPM request. Production admission and the paused live comparison remain
unchanged.

## Offline inspector follow-up and test readiness

The inspector's complete 2,048-entry map confirms the `B2/B3` and `E7`
read-only aliases above. Their meanings are independently supported by cycle
initialization `0x14772..0x1477A` and fallback selection `0x162CA..0x162DC`.
The map contains no exact-address entry for last-normal mode `0x8034F` or
saved modes `0x86062/0x86063`. This is an absence from this bank map, not proof
that no other interface can expose those fields. It prevents treating the
permission-clear fallback as an independently verified baseline restore.
The MCU explorer's `0x100000xx` fields are internal RAM annotations, not
host-readable EC register indices.

Before a direct-duty write experiment, the remaining work is concrete:

1. Establish an exclusive or quiescent transfer interval despite normal MCU
   work, including bank ownership and pending-write reconciliation.
2. Implement typed confirmation that checks both response statuses and obtains
   actual EC values, separately from cached intent, PWM and tachometer RPM.
3. Establish a direct restore of captured mode, manual flags, requests and
   override state, preserving other flag bits and installed intent. Permission
   clearing alone is insufficient; unmapped fallback inputs remain unknown.
4. Define credible recovery if the test host or transfer fails. Existing
   PROCHOT/GPIO responses do not establish timely fan restart, and stale sensor
   values can survive. Host-independent automatic recovery is required for an
   unattended switch; an attended experiment still needs a justified recovery
   procedure and bounded exposure rather than assuming a host `finally` works.

The next offline target is MCU ownership and completion, before preparing a
read-only observation harness. Even a generic read needs admission: its access
gate changes MCU state and its bus transaction shares the worker's storage.
This does not authorize a gate, bank or duty write. Earlier RPM-test approvals
do not cover this different register-control path.
