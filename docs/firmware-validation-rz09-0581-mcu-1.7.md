# RZ09-0581 MCU 1.7.0.0 validation

On 2026-09-21, the installed Razer Blade 16 RZ09-0581 passed the firmware
smoke suite and attended physical checks with BIOS 4.01, EC 1.04, and MCU
1.7.0.0. This records tested firmware provenance; it does not change hardware
write admission or enable commands for another model.

## Tested software

- Service and Session Agent: `3d59cd20aafd84be59a2d8182289c4ba9406af30`.
- Tray: `5a1bd1fd9c02d89123b47bb58a919823fd60bc9a`.
- FirmwareSmoke: built from the installed Service's exact source revision.

The older pre-update snapshot used different installed software and contained
a controller-conflict flag, so it could not establish a controlled firmware-only
comparison. Reversible testing instead used a fresh, passing snapshot on MCU
1.7.0.0 through the tool's current-baseline `rehearsal` command. The existing
pre-update comparison was not modified or treated as passing.

## Results

- The complete retry suite passed 57 checks with no failures or skips. Its eight
  reversible tests covered performance mode, HyperBoost, fixed fans, charge
  limit, Static lighting renderer, startup animation, function-key primary
  mode, and Gaming mode. All recorded controls matched their starting values
  afterward, with no unconfirmed writes or external-change flags.
- The operator confirmed keyboard Off, Static, and animated lighting; keyboard
  and display brightness; media, Project, and Print Screen keys; the performance
  key and notification; configured M1-M5 actions; and microphone mute and its
  indicator. Lighting output relies on this physical observation because the
  laptop does not report the resulting lighting state.
- Battery and USB-C transitions worked. Independent snapshots confirmed
  Battery/BatterySaver and UsbC/Balanced with Automatic fans. Their only failed
  health check was the expected full-power-adapter requirement. Battery
  discharge was observed on USB-C; sustained charging was not established.
- After returning to the full-power adapter, the operator approved and performed
  one sleep/resume cycle and confirmed recovery of lighting, keys, the AC
  profile, and readings. The post-resume health check passed all 33 required
  checks, and recorded controls matched the pre-sleep snapshot.

The final optional warning was unavailable GPU temperature while the GPU slept.
This is representative smoke coverage, not an exhaustive test of every setting
or long-term stability.

## Recovered transient failures

The first run stopped at the fixed-fan test after an indeterminate native I/O
failure on fan target channel 2. Automatic fans were restored and independently
confirmed before the operator authorized another run. The complete retry passed.
Performance and HyperBoost also encountered indeterminate submissions that the
Service reconciled through fresh readback. Later Gaming-mode, startup-animation,
and fan readback gaps, plus a performance-change flag during power transitions,
cleared on read-only rechecks. These observations do not establish their cause
or demonstrate that MCU 1.7.0.0 introduced them.

Only this sanitized summary is committed. Machine-local snapshots and diagnostic
archives remain private.

## Temporary full charge and Power Mode Control (07/8F, 07/0F)

On the RZ09-0581, `07/8F` reads and `07/0F` writes **Power Mode Control**, a
single byte shared by several settings. The bit layout comes from a read-only
inspection of the installed Synapse 4 code for this model:

| Bit | Setting |
|---|---|
| `0x01` | CPU boost |
| `0x02` | Maximum fan speed mode |
| `0x04` | One-time override: "Allow system to fully charge once" (temporary full charge) |
| `0x08` | Local dimming (mini-LED panel) |

No higher bits are defined. Synapse changes one setting by reading `07/8F`,
changing one bit, and writing the combined byte to `07/0F`. With local dimming
on, for example, it clears the override by writing `08` and enables it by
writing `0C`.

### Field evidence

On 2026-10-14 a reporter's RZ09-0581 (BIOS 4.01, MCU 1.7.0.0, OpenBlade
0.26.2-nightly.87, plugged in) returned `08`, which OpenBlade then rejected
because it accepted only `00` and `04`. OpenBlade and Synapse ran one at a time,
and each OpenBlade export was taken with Synapse closed.

| Step | Synapse "Allow system to fully charge once" | OpenBlade `07/8F` |
|---|---|---|
| A | Unchecked; Battery Health Optimizer on at 80 percent | `08`, rejected; battery controls blocked |
| B | Checked | `04`; OpenBlade cancelled to its saved state and read back `00` |
| C | Unchecked | `00`; 80 percent limit, protection on, full charge off |

By the bit table, step A is full charge off with local dimming on. In step B
OpenBlade read `04` rather than `0C`; we infer, without direct evidence, that
local dimming had been turned off before that read. `08` is not a "full charge
completed" marker; that earlier hypothesis is withdrawn.

### Local full-charge test

A local test on 2026-10-14 used a second RZ09-0581 (MCU 1.7.0.0), plugged in
throughout, with Synapse not running. OpenBlade enabled temporary full charge at
79 percent, and the readback confirmed `04`. The battery reached 99 to 100
percent after about 50 minutes. For the next 100 minutes `07/8F` still read
`04`, and Windows never reported the battery as fully charged. OpenBlade then
cancelled the mode, the readback confirmed `00`, and the saved 80 percent limit
and protection state were unchanged.

This shows only that `0x04` persisted for about 100 minutes at 99 to 100
percent with Synapse closed. Windows never reported the battery as fully
charged, so the test does not show what firmware does after a battery-full
event. Separately, the read-only inspection shows that the Synapse code clears
the override when it sees a battery-full event.

### OpenBlade behavior

- Reading: on the exact RZ09-0581 (`1532:02E0`) only, any `07/8F` value made of
  the four defined bits decodes, and temporary full charge is bit `0x04`. Wrong
  lengths and undefined bits (`0x10` to `0x80`) are still rejected and the
  rejected bytes are kept in diagnostics. The RZ09-0528 and other evidence still
  accept only `00` and `04`.
- Writing: OpenBlade's only captured payloads are enable `04` and cancel `00`.
  Each is a whole byte, so by the bit table it would also turn off any other
  setting that is on; that combination has not been captured. Before every `07/0F` write, explicit or automatic, the port reads
  `07/8F` again in the same write session and sends the write only if that read
  is `00` or `04`. If CPU boost, maximum fan speed, or local dimming is on, the
  write is refused before anything is sent, and the result says that OpenBlade
  won't change temporary full charge because it would also change those
  settings. A failed or rejected pre-write read also sends nothing.
- OpenBlade does not write combined payloads such as `08` or `0C`. Read-modify-write
  needs capture and reversible validation of those payloads before it can be
  admitted.
