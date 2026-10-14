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

## Temporary full charge reading 08 (Synapse shows it as off)

On 2026-10-14 an RZ09-0581 (BIOS 4.01, MCU 1.7.0.0, OpenBlade 0.26.2-nightly.87)
returned `08` for the temporary-full-charge query (07/8F), which OpenBlade rejected
because it accepted only `00` (off) and `04` (on).

- Synapse 4 "Allow system to fully charge once" unchecked, Battery Health Optimizer
  on at 80 percent: 07/8F returned `08`.
- Checking the box in Synapse: OpenBlade read `04`, reconciled to its saved desired
  state, cancelled, and read back `00`.
- Unchecking it again: 07/8F returned `00`, with the battery confirmed at 80 percent,
  protection on, and full charge off.

Synapse therefore presents `08` as off. OpenBlade never writes `08`; any 07/0F write
clears it. Admission is scoped to the exact RZ09-0581 (`1532:02E0`) through its model
read evidence: exactly `08` decodes as inactive, and every other value, and `08` on
the RZ09-0528, stays rejected. Writes are unchanged (enable `04`, cancel `00`).

Unproven hypothesis: firmware sets `08` when a temporary full charge completes at 100
percent and Battery Health Optimizer re-enables. To confirm, enable full charge once
below 100 percent while plugged in, let it reach 100 percent, export state with Synapse
closed, and expect `08`.

A first local attempt on 2026-10-14 was inconclusive. The test used a second RZ09-0581
(MCU 1.7.0.0), plugged in throughout, with Synapse not running. OpenBlade enabled temporary
full charge at 79 percent, and the readback confirmed `04`. The battery reached 99 to 100
percent after about 50 minutes. For the next 100 minutes, 07/8F still read `04` and
Windows never reported the battery as fully charged. OpenBlade then cancelled the mode,
the readback confirmed `00`, and the saved 80 percent limit and protection state were
unchanged. `08` never appeared. It may need a complete charge, an unplug, or the Fn+B
shortcut path. The admission above rests on Synapse showing `08` as off, not on this
hypothesis.
