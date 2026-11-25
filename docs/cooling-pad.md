# Razer Laptop Cooling Pad

OpenBlade controls the Razer Laptop Cooling Pad RC21-0230 (`1532:0F43`) from the
Cooling pad page in the left menu. The page appears when a connected pad is
confirmed and disappears when unplugging is confirmed. A temporary connection
check failure keeps the last confirmed page visible with writes disabled.
Support is source-backed experimental. The pad is admitted by its own exact USB
identity; pad commands do not depend on the laptop model. Laptop controls retain
their separate model admission. Connect one pad and choose a fan or lighting
setting; each change applies after a short pause, like the laptop controls, and
the page refreshes the pad status while it is open. Until you choose a setting,
the pad keeps its own settings. The page always shows the settings the service
is running, including ones changed by another OpenBlade client; a change you are
making takes precedence until it applies. Reconnecting the pad does not start
control automatically.

OpenBlade hands the pad back to its own settings when the laptop sleeps or shuts
down and when the service stops, for example during an update. Once Windows is
awake and you are signed in again, it applies your cooling-pad settings again
when **If settings change outside OpenBlade** is set to **Restore them**.
**Release control** stops this until you choose a setting again.

While OpenBlade controls the pad, it reads the pad every few seconds. If its fan
mode, lighting effect or brightness changes elsewhere, such as in Synapse or
with the pad's lighting button, **Restore them** applies your settings again, up to
three times in ten minutes. Lighting is restored only when **Override external
lighting changes** is on. Otherwise the pad's new lighting stays and OpenBlade
keeps controlling the fan. Choose a lighting setting again
to manage it. OpenBlade releases control instead under **Notify me**, after a
fourth change, for a lighting change when it controls only the lighting, or when
your settings cannot be applied again. The
changed part stays as it is, the parts OpenBlade changed return to the pad's
own settings, and control stays off until you choose a setting.

OpenBlade can't see the pad's fan button. In Automatic and with a curve,
OpenBlade keeps setting the fan speed, so a press lasts only a moment. To use
the fan button, choose Fixed: the pad keeps the speed you pick with the button,
and the page still shows your Fixed speed. **Release control** hands the fan
back to the pad. The fan button also changes the pad's lighting, which OpenBlade
handles like any other lighting change.

A change the service refuses before sending it, for example while Windows
settles after waking, is tried again a few times. An unconfirmed change pauses
the page's controls until a read confirms the pad's state; the page checks
automatically while it is open, and **Check** reads it at once.

Choose Automatic with a Quiet, Balanced or Performance profile, a fixed target
from 500 to 3200 RPM in 50-RPM steps, a manual fan curve, or Off. Automatic and
manual curves follow the CPU, the GPU, or both; with both, the higher demand wins.
Curve input uses the same smoothed temperatures as the laptop's manual fan
curve, so a brief package spike does not change the fan speed. When the
selected CPU stays at 95 °C or the GPU at 87 °C for 2 seconds, Automatic and
curves run the pad at full speed until it drops 3 °C. Fixed targets come from the selected mode configuration; Smart targets are the
ramping speed the firmware reports. Neither is measured fan speed. A missing or stale
temperature reading pauses curve updates; the pad keeps its last speed and control
continues. A GPU that Windows reports powered down is ignored: with both sources the
curve follows the CPU, and a GPU-only curve runs at its first point. Curves use the same CPU package and GPU readings as the
laptop's manual fan curve. Stop competing pad controllers before applying settings.
Any running Razer service blocks pad writes, except Razer Experience Service, a
model runner that Razer's driver packages install; it has no HID device access.

Lighting supports Off, Static, Breathing, Spectrum and Wave, with brightness and
colors. Keyboard sync projects OpenBlade's host-rendered keyboard frames onto
the pad's 18-LED strip. Firmware-rendered keyboard effects have no host frame to
mirror. The projection averages keyboard columns; it is not a physical key map.
The pad's lighting button can also select built-in effects OpenBlade can't read.
While one shows, OpenBlade keeps controlling the fan and leaves the lighting as it
is. To change the lighting in OpenBlade, first choose another effect with the
pad's button.

**Release control** restores the saved fan preset, configured target, lighting
effect and exact raw brightness for the fields OpenBlade changed. It leaves
untouched fields alone. An existing custom-frame effect cannot supply its pixel
baseline through the reviewed query API, so OpenBlade refuses to overwrite that
frame buffer. Lighting control requires a readable built-in effect baseline.
Select a built-in effect with its owning controller first. A retained protected
baseline from OpenBlade's own mirror session can support explicit mirror recovery
after a service restart; unreadable current pixels are never treated as saved.

Both persistent profile 1 and volatile profile 0 effect parameters are saved. Pixel data from another custom renderer remains unreadable.
Byte 0 of `0F/02` is the store flag that openrazer and padctl call VARSTORE (`1`)
and NOSTORE (`0`); the strip shows whichever effect was written last. When the
two records hold different effects, a read cannot tell which one is showing on
its own. The service then reports the effect it wrote last and confirmed while
that record still holds it, which covers its own Apply (profile 1), keyboard
sync (profile 0) and restoration (profile 0 when the baseline has it). Otherwise, including after a restart before its next lighting
write, or after a controller or identity boundary, the lighting mode is
unconfirmed instead of read from profile 1 alone.

The service resumes control only after a clean service-owned handback, and only
under **Restore them**. It keeps a
per-user resume record beside the saved settings, written by an Apply that takes
control and removed by **Release control**. A retained restoration journal,
paused writes, a failed Apply or a configuration failure all wait for the user.
Resumption uses the same session, power and per-command admission as an Apply,
checks a closed admission again each second, and waits five seconds after an
attempt that reached the pad and was refused. Shutdown cleanup is never undone.

An Apply refused before submission saves nothing: the saved request, resume
record and restoration journal stay as they were, so maintenance and resumption
cannot later apply a change the page reported as not accepted. Once the request
is saved, a native deferral before the first write is a failure that leaves
writes available. Outside-change detection compares the fan record of the running owner,
both effect records, raw brightness and device mode with the read taken after
the last confirmed write; the fan demand and ramping speed are not compared.
A fan write in maintenance runs only after a fresh comparison, because selecting
a preset would otherwise hide the change. A failed comparison read defers that
tick, up to two in a row. A third failed read is handled like a lost
connection, described below: the session is closed and reopened. A refusal, such
as from a competing controller, stops control as a failed operation. Checks run every five seconds,
each one full configuration read. The restore allowance is held in memory and
starts again after a service restart or an accepted Apply. A policy upgraded from
an earlier version restores nothing, and does not resume, until its disclosure
has been seen. The firmware cannot report which owner's fan
record is running, so a change written under the other owner is not detected.
The pad's own fan button is not detected either. On firmware 1.10.00 the fan
changed on a press while the record OpenBlade compares stayed the same: the
firmware owner's in Automatic and the Synapse owner's in Fixed. The product
middleware declares input report 5 for pad button events (`CycleFanSpeedMode`,
`TurnOnFan`, `TurnOffFan`), and Synapse writes a button mapping mode (`209` for
the fan button). In Normal mode, a read-only listener on the readable input
collections received no report during fan and lighting presses; the
consumer-control collection is held by Windows. OpenBlade adds no button-mapping write or
Driver-mode entry to detect the button. In Automatic and a host curve, the next
`0D/01` demand replaces the button's speed; Fixed sends no demand, so the
button's speed stays while status reports the confirmed record.

Without the lighting opt-in, a lighting change found during fan control is kept:
the ownership journal drops its lighting part, so **Release control** and service
handback restore only the fan, and later checks, restorations, reconnects,
keyboard-sync frames and resumption leave lighting alone. The user's next Apply
manages lighting again, and **Release control** or a user change ends the kept
state. The kept state is held in memory, so after a service restart resumption
applies the saved lighting again. When the saved settings control only the
lighting, there is nothing left to control and the change releases control.

A status read submits nothing. When one times out or finds the session retired,
control and the last confirmed status, including its message, are kept and
maintenance reconnects. A control port that cannot be opened, or a refusal, is
still reported. While control runs, a successful status read keeps the latest
control message, and after an outside change or a lost connection stops control
it keeps that reason. Otherwise it reports that the configuration was read.

A timed-out exchange retires the HID session, and its later commands fail as
`Disposed` without reaching the pad. Reads, Apply and Release then open a fresh
exact-device session. Three failed comparison reads in a row close the
session the same way, even when it has not retired itself. A new session cannot tell which fan owner or effect record
the old one confirmed, so during control it does not compare those. **Restore
them** applies the saved settings again, counted against the same restore
allowance; **Notify me** stops control. Without the lighting opt-in, lighting is
rewritten only when the stored effect, brightness and device mode still match;
otherwise it is kept as for an outside change and only the fan is applied again.

A failed or ambiguous submission pauses control. Read the current state before
choosing an explicit recovery action. The tray's automatic read is read-only and
never resends the change. OpenBlade retains a write-ahead recovery
record across service restarts; it does not replay the last demand or erase an
unresolved operation. User changes restore the original protected baseline
before binding the new user; unresolved restoration remains visible.
Power/session transitions revoke queued writes. A power transition is a Windows
AC/battery notification; every write also checks the current Windows power source.
The laptop's periodic adapter classification does not revoke pad writes. This
implementation has simulated validation; new physical acceptance is separate.

## Source review

Vendor definitions take precedence over community implementations. The reviewed
Synapse product is decimal **3907**, exactly `0F43`:

| Source | SHA-256 / revision | Used for |
| --- | --- | --- |
| [Razer product middleware](https://apps.razer.com/synapse/products/3907/mw/main.cf8ec2633f46f2905636.js) | `A4EEBB94C1F35A3F4DDDC8D75986CA4528447C2BBC7F57F03DDA74C25B6419A2` | Exact product, interface 0, product tasks and default profile |
| [Razer shared bundle served for this product](https://apps.razer.com/synapse/products/3907/mw/6259.b736650c7b576a9ca2ed.js) | `B4A3C6C8F2724DC4F40D3371187BC041F1FBA5480A891A062E7302BD59C51EDE` | Thermal mode/target, regional lighting geometry, effects, brightness and frames |
| Attached `LaptopCoolingPad_0F43_FirmwareUpdater_v1.10.00_r1.exe` | `7383483013521C788B98CAAFB5FE7DA813354A51C696C5A62E9BA6D00D84DF7D` | Signed Razer carrier; static firmware descriptor corroboration |
| [padctl](https://github.com/hbmartin/razer-cooling-pad-mac/tree/79fe6e773cfd95cbfa3a2defacb02b5b9d3acf48) | `79fe6e773cfd95cbfa3a2defacb02b5b9d3acf48` | Independent exact-pad fan and lighting implementation; transaction selections |
| [Exact-device OpenRazer proposal](https://github.com/christophehenry/openrazer/tree/b14ea97f78331beff351a9beb789b7a40edfacc8) | `b14ea97f78331beff351a9beb789b7a40edfacc8` | Proposed 1x18 geometry; not merged or physically validated by OpenBlade |

The updater was parsed as data. Its Authenticode signature verified as Razer USA
Ltd. `DeviceUpdater.resources` has SHA-256
`4BB7991A7CA69F9A2FE62604401723B0AEBD758CE3B90C845040344319040767`.
All 2049 Intel HEX checksums were checked. The reconstructed firmware image has
SHA-256 `8C8EACF545202F9424C8EC29612E42976E8C1798508149BDE936C3390F414CD0`.
The device descriptor at image offset `0x7928` identifies `1532:0F43`, revision
`0200`. The feature descriptor at `0x793A` has no report ID and contains 90 bytes:
Windows adds the zero report-ID prefix for 91 bytes. Report ID 5 belongs to the
following input descriptors. The carrier and extracted firmware remain outside
Git. OpenBlade does not run or flash this updater.

Because interface 0 declares exactly one feature report, any other `1532:0F43`
interface-0 collection without a feature report is input-only and cannot be the
control channel. An installed Razer driver stack re-exposes such collections
under its virtual consumer device. Discovery and the control opener skip them
without opening them for I/O, whatever usage or revision the driver assigns. A
non-exact interface-0 collection with a feature report, or one that cannot be
read, still blocks discovery and control.

The updater also contains a generic keyboard `update_config.ini`; its PID02B0
is not the attached device's identity. Resource/firmware descriptors establish
the pad identity independently of that unrelated configuration.

## Reviewed command contracts

All commands remain internal typed operations. The shared session verifies the
response status, checksum, transaction, command and bounded semantic payload.
No raw firmware API, probing write, fallback transaction search or vendor driver
is introduced.

| Command | Source module | Meaning |
| --- | --- | --- |
| `0D/90` | 63472, 91655, firmware | Read one owner's selection: `[owner,preset,speed]` for Low/Medium/High, `[owner,preset]` otherwise |
| `0D/10` | 63472, 91655, 87887, firmware | Set an owner's selection directly; it becomes the running preset |
| `0D/81` | 63472, 91655, firmware | Read the ramping speed for class 1, fan 5; not a tachometer |
| `0D/01` | 91655, exact-pad scheduler | Set class 1, fan 5 target in 50-RPM units |
| `0F/80` | 65430, 9069, firmware | Read the five-byte region record; admit one row of 18 LEDs |
| `0F/84`, `0F/04` | 65430, 9069, 85191 | Read actual first-region brightness; write profile 1, region 0; preserve raw 0..255 baseline |
| `0F/82`, `0F/02` | 65430, 79151, 9069, 85191, firmware | Read and set effect, flags, rate, color count and RGB values |
| `0F/03` | 65430, 9069 | Stream profile 0 frames with region/row/start/end and bounded RGB data |
| `00/84`, `00/04` | 9069, product initialization | Read/set device mode; mirror entry uses Driver directly and saves prior mode |

Ownership is firmware `0` or Synapse `1`. Presets are Low `0`, Medium `1`, High
`2`, Quiet `3`, Balanced `4`, Performance `5`, Off `6`. Fixed control directly
requests `[1,1,target/50]`; Off requests `[0,6,0]`. A host curve preserves an
existing Smart preset or directly selects Quiet `[0,3,0]`, then sends calculated
`0D/01` demands. Automatic selects the profile's Smart preset `[0,3..5,0]`
directly and sends `0D/01` demands from the Synapse tables below. No preliminary Fixed, Medium, Automatic or Normal reset is
required. Source initialization enters Driver `03 00` directly; ordinary pad
cleanup in Synapse skips a blanket Normal handoff for this Chroma device.

The firmware 1.10.00 image (class `0D` handler at `0x3144`) defines the fan
replies:

- `0D/90` (`0x3228`) treats byte 0 as an owner selector. The firmware (`0`) and
  Synapse (`1`) owners each keep their own last selection. The reply echoes the
  selector, then the preset, then the speed only for Low, Medium and High. Other
  selectors fail.
- `0D/10` (`0x3290`) accepts presets 0 to 6 from the firmware owner and only
  Low, Medium and High from the Synapse owner. Those three need a speed from
  500 to 3200 RPM and share one speed per preset between both owners. The
  selection written last runs; no command reports which owner that was.
- `0D/81` (`0x3190`) reports the ramping speed, and the target only once the
  two are within 50 RPM. It cannot confirm a new `0D/01` demand; the verified
  echo does.

Fan readback therefore queries the owner OpenBlade just wrote. Until a session
confirms its own fan write, status reports the firmware owner's selection.

The same image (Cortex-M0, class `0F` handler at `0x64E0`) defines the lighting
replies:

- `0F/80` (`0x657C`) returns exactly `[05,19,03,01,12]`: region 5, then one row
  at byte 3 and 18 LEDs at byte 4.
- `0F/82` (`0x65BA`) returns twelve bytes: a constant zero, the stored region
  id, effect, flags, rate, color count, color slot 1 and color slot 2. Byte 0 is
  not the profile. The request's profile and region select the record.
- `0F/02` (`0x6E40`) stores only the effect id for effect 8. Other effects store
  flags, rate and count, color slot 1 only for Static, and color slot 2 from
  every request. Slots beyond the count keep earlier bytes.

Readback compares only those stored fields. The firmware also accepts effects
7 and 9, which OpenBlade does not admit. The pad's lighting button selects them,
so a well-formed `0F/82` record holding 7 or 9 is accepted and read as unknown
lighting: the snapshot's effect configuration and lighting mode are null, and the
fan, brightness and device mode still read. Any other effect id fails the reply.
A lighting Apply is refused while either record is unknown, because Release could
not restore it; a lighting readback that reads as unknown fails as a readback
mismatch.

Brightness GET resolves requested region 0 through the regional descriptor to
the first actual region (historically 5), while SET retains region 0. That is a
source-defined asymmetry, not a reason to try multiple write layouts.

Fan operations use the pinned exact-pad upstream transaction ID `02`; lighting
uses `1F`. One exchange runs at a time. Custom frames use the exact vendor
SetFeature-only streaming contract; native submission is not pixel readback.
No response retry or guessed acknowledgement is added. Geometry is admitted
from typed descriptor readback before frames, not inherited from another pad.

The separate vendor `0D/88` current-speed name was found in a shared module,
but its exact-pad caller and physical meaning are not established here. It is
not exposed as measured RPM. Physical speed, thermal performance, arbitrary
firmware revisions and display-off behavior have not been newly validated.

## Automatic mode

Automatic follows Synapse's Smart scheduler from the pinned shared bundle
(module 74967, product task runner in `main.js`):

- Entering Smart writes `setThermalFanCoolingMode(smart.active, 0, 0)`: `0D/10`
  with the firmware owner and preset Quiet `3`, Balanced `4` or Performance `5`.
- Each system-monitor sample evaluates a step table: the speed of the point with
  the smallest temperature strictly above the reading, without interpolation.
  With so few points, that is one speed across normal laptop temperatures:
  Balanced CPU is 2200 RPM from 40 to 89 °C. `cpu` uses the CPU table with the package temperature, `gpu` the GPU table,
  and `cpu_gpu` the higher of both results.
- The demand `floor(rpm/50)` is sent with `0D/01` only when it changes.
- Synapse's default profile is Fixed with `smart.active` Balanced and source
  `cpu`; its Fixed Low, Medium and High are 1600, 2200 and 2700 RPM.

| Profile | CPU (°C:RPM) | GPU (°C:RPM) |
| --- | --- | --- |
| Quiet | 40:500, 50:500, 85:1000, 90:1600, 100:1600 | 30:500, 40:500, 75:1000, 80:1600, 90:1600 |
| Balanced | 40:1000, 90:2200, 100:2200 | 30:1000, 80:2200, 90:2200 |
| Performance | 40:1000, 55:2000, 80:2700, 100:2700 | 30:1000, 45:2000, 70:2700, 90:2700 |

OpenBlade differs in five ways. The profile and temperature source are
selectable. Tables are evaluated on smoothed temperatures. OpenBlade
interpolates between the table points in 50-RPM steps, like a manual pad curve,
so the speed follows the temperature: Balanced CPU gives about 1500 RPM at
60 °C and 1850 RPM at 75 °C. Below the first point it uses the first point's
speed. Above the last point,
Synapse yields no speed and sends 0; OpenBlade keeps the last point's speed. The
preset write is skipped only when this session already confirmed that exact
preset. Automatic is source-backed and software-tested; it has not been
physically validated on a pad.

## Curve temperature smoothing

The service conditions CPU and GPU readings for every host-driven fan curve:
the laptop's manual fan curve on every model that admits it, and the pad's
manual curve and Automatic mode. Each sensor is smoothed exponentially over the
actual elapsed time with a 5-second time constant, so a one-second +30 °C spike
moves the input about 5 °C, while 95% of a sustained change is followed
within 15 seconds. A falling reading must drop 3 °C below the held value
before the input follows it. There is no timed cooldown hold. A missing sensor
or a sample gap over 10 seconds starts a fresh history. Safety decisions keep
using the raw readings: the laptop curve still forces maximum speed immediately
for a missing CPU reading, an unconfirmed missing GPU, CPU at 95 °C or GPU at
87 °C, and the pad's missing-reading pause checks the raw reading. The pad's
Automatic mode and manual curve use the same emergency limits on the raw
readings: a selected CPU at or above 95 °C, or a selected, powered GPU at or
above 87 °C, for three consecutive one-second samples (2 seconds) sends
3200 RPM whatever the table or curve says. A shorter spike does not trip them.
Full speed holds until that reading drops 3 °C below its limit, so a reading at
the limit does not switch the speed back and forth. The smoothed input is not
used here because it approaches a steady reading gradually and would take 12
to 20 seconds to reach the limit. Synapse has no such limit. Fixed targets read
no temperature and keep their speed.

## Typed cooling-demand API

Turn on **Allow apps to request more cooling** to opt in. It is off by default.
The authenticated current-user service pipe exposes explicit demand leases only
while that opt-in and a user-started Fixed, curve or Automatic session are active.
Leases are bounded to eight, expire within 30 seconds, and belong to the
requesting Windows process identity. They can raise demand while a user-started
fan session is active; they do not start an inactive session. Settings, desired
lease demand, confirmed configured target and measured-speed availability remain
separate. See the typed request definitions in `OpenBlade.Contracts`.

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
This implementation supersedes the stale capture-only admission design in draft
PRs #125 and #621; historical physical results retain their original scope.
