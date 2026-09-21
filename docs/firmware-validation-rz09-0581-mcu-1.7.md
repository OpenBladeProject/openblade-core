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
