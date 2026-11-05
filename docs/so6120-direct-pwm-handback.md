# SO6120 direct-PWM handback to Automatic

**An explicitly applied Silent Automatic command can leave direct-duty mode.**
The EC can resume normal fan feedback without switching through another profile
or clearing the direct-duty permission first. This establishes a candidate for
the ordinary switch-off operation. It does not establish that reselecting the
current UI option sends that command, or that the entire research state has
been restored.

This offline review uses the pinned SO6120 EC 1.04 and MCU 1.07 images in the
[bridge analysis](so6120-mcu107-ec-fan-bridge.md#exact-inputs-and-method), and
OpenBlade source at `628c096076f23f588a6404d1fffa26a3fdfba2fb`. No hardware,
service or installed configuration was accessed. Physical handback remains
untested; other models do not inherit these addresses or semantics.

## Why the normal command can work

The exact-device `performance-silent` fixture requests normal selector `5`
and fan flag `0` through the ordinary thermal-mode command. MCU handler
`0x9798..0x97B2` accepts normal selectors, puts the requested mode and flag in
cache `0x10000228/0x10000229`, and queues mode work.

With shared EC bank 0 selected, worker `0x4D56` writes mode index `1A`, then
`0x4D6C` writes mailbox command
5 to index `0A`. After bus completion it queues a separate manual-flag write
to index `36` at `0x4C78`. That branch occurs earlier in the worker, so a newly
queued flag update needs a later pass. The worker does not itself force bank 0;
another selected bank can change those destinations. Mode and Automatic are
separate writes.

Once the EC selector at `0x8035F` really holds `5`, dispatch
`0x15E66..0x15E80` no longer takes the `FE` branch. Active-state and cadence
gates permitting, supervisor `0x158AE..0x158B6` calls ordinary PWM feedback
`0x15510`. Permission `0x80425.6` does not block this normal feedback path.
Automatic additionally requires the normal manual state to be restored;
the selector alone does not prove it.

Mailbox command 5 is a policy notification. Its exact jump-table byte at
`0x9030` is `06`, selecting `0x9032` from table base `0x902C`. The branch
clears `0x80941`, sets `0x8033F.0`, then clears command `0x80940` at `0x9086`.
Scheduled work `0xC704..0xC71E` evaluates policy and records status 4. It can
run before the separate manual-flag write, and the evaluator can return early.
Status 4 is not proof of Automatic or settled PWM.

Returning to the same last-normal selector can reuse the resident table.
`FE` clears stage bookkeeping but retains last-normal selector `0x8034F`.
Reload depends on additional refresh/manual-transition gates; a different
profile can reload policy and saved manual state. There is no established
requirement to visit Balanced or another profile first. Normal Automatic may
legitimately leave the fans stopped at idle, so fan rotation alone is not the
definition of a successful handback.

## What still needs confirmation and cleanup

| State | Effect of the ordinary command |
|---|---|
| EC selector and normal manual state | Intended normal destination; confirm both in the EC after pending work settles. |
| Actual PWM and tachometer RPM | Subsequent results of normal control; neither is an immediate command acknowledgment. Do not write an old sampled PWM value back over Automatic control. |
| Permission byte `0x80425` | Remains unchanged. Bit 6 can still suppress recomputation of non-fan contributor `0x80493` under its normal gates. |
| Staged duty `0x80394/0x80395` | Remains stored, though the normal selector stops consuming it through `FE`. |
| Generic MCU gate and changed bank state | Not closed or restored by the normal thermal command. |
| Targets and saved normal state | A profile change can affect them; restore captured intent rather than substitute defaults. |

The [register review](so6120-ec-register-semantics.md) documents why replacing
the whole permission byte with zero is not a valid cleanup shortcut. A complete
restoration must account for the state the experiment changed, including
non-fan effects; firmware-owned Automatic PWM is dynamic.

There are two concrete completion hazards in addition to the previously
reviewed shared transport:

- The normal SET and GET share the MCU cache. SET preserves existing pending
  reads. If mode read bit 17 is pending, `0x4D2E/0x4D3A` can overwrite the
  requested normal mode with the current EC value, including `FE`, before
  `0x4D56` writes it. Manual read bit 18 can similarly replace the requested
  flag at `0x4C4C/0x4C58`. `0x5198..0x51A0` can arm these reads on a changed
  fallback-source bit. This is a conditional static path, not an observed
  failed transition. A matching cached GET is not independent EC confirmation.
- With bank zero, the first payload for index `36`, and existing manual flag
  bit 7 set, receiver `0x2546..0x256A` stages the byte instead of performing
  the mapped store at `0x2576`. Its pending-event callback `0xCD6C -> 0x9008`
  handles the mailbox; the inspected path does not apply that staged flag.
  Later normal policy can update flags independently. A bus acknowledgment
  therefore does not establish the requested Automatic flag.

## Current UI and future switch behavior

The current [thermal decoder](../src/OpenBlade.Protocol/ThermalModeProtocol.cs)
does not admit `FE`. The production performance and fan setters perform a
typed pre-read, so an actual `FE` response can block submission. Conversely,
a stale cache that already looks like the destination can take an
`alreadyMatches` or non-manual early return in
[ProductionBatteryPort](../src/OpenBlade.Protocol/ProductionBatteryPort.cs).
Reselecting a UI option is consequently not a guaranteed exit from raw `FE`.

A future exact-device PWM switch should have an explicit handback operation
for its owned direct-duty state, using the intended normal destination and
independent confirmation. It must preserve fresh write admission and stop on
ambiguous submission; this finding does not justify weakening the production
decoder or retrying an uncertain SET.

Orderly handback and host-loss recovery remain distinct. The user-proposed
Automatic command addresses an operating host that can send and confirm it.
An unattended switch still needs independent cooling recovery. An attended
experiment needs a justified abort/recovery procedure and bounded exposure;
it is not required to prove an unattended watchdog merely to establish the
ordinary exit. Transport ownership and actual-state confirmation remain
unresolved before any raw-register test.
