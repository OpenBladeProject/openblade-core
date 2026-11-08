# Razer Laptop Cooling Pad

OpenBlade controls the Razer Laptop Cooling Pad RC21-0230 (`1532:0F43`) from the
Cooling pad page in the left menu. The page appears when a connected pad is
confirmed and disappears when unplugging is confirmed. A temporary connection
check failure keeps the last confirmed page visible with writes disabled.
Support is source-backed experimental. The pad is admitted by its own exact USB
identity; pad commands do not depend on the laptop model. Laptop controls retain
their separate model admission. Connect one pad, choose its settings, then select
**Apply**. Starting
OpenBlade or reconnecting the pad does not start control automatically.

Choose a fixed target from 500 to 3200 RPM in 50-RPM steps, turn the fan off, or
use a CPU curve, a GPU curve, or both. With both selected, the higher demand wins. Fixed targets come from the selected mode configuration; Smart targets come from
the separate host-demand register. Neither is measured fan speed. Missing or stale temperature readings
pause curve updates. The shared CPU temperature source currently rejects the
firmware's constant readings, so CPU and combined CPU/GPU curves remain unavailable
until a supported changing CPU reading is provided. GPU-only curves can use an
active GPU's fresh temperature. Stop competing pad controllers before applying settings.

Lighting supports Off, Static, Breathing, Spectrum and Wave, with brightness and
colors. Keyboard sync projects OpenBlade's host-rendered keyboard frames onto
the pad's 18-LED strip. Firmware-rendered keyboard effects have no host frame to
mirror. The projection averages keyboard columns; it is not a physical key map.

**Release control** restores the saved fan preset, configured target, lighting
effect and exact raw brightness for the fields OpenBlade changed. It leaves
untouched fields alone. An existing custom-frame effect cannot supply its pixel
baseline through the reviewed query API, so OpenBlade refuses to overwrite that
frame buffer. Lighting control requires a readable built-in effect baseline.
Select a built-in effect with its owning controller first. A retained protected
baseline from OpenBlade's own mirror session can support explicit mirror recovery
after a service restart; unreadable current pixels are never treated as saved.

Both persistent profile 1 and volatile profile 0 effect parameters are saved. Pixel data from another custom renderer remains unreadable.

A failed or ambiguous submission pauses control. Read the current state before
choosing an explicit recovery action. OpenBlade retains a write-ahead recovery
record across service restarts; it does not replay the last demand or erase an
unresolved operation. User changes restore the original protected baseline
before binding the new user; unresolved restoration remains visible.
Power/session transitions revoke queued writes. This
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
| `0D/90` | 63472, 91655 | Read `[ownership,preset,speed]`; zero selector matches the generic omitted/default selector |
| `0D/10` | 63472, 91655, 87887 | Set saved or requested fan configuration directly |
| `0D/81` | 63472, 91655 | Read configured target for class 1, fan 5; not a tachometer |
| `0D/01` | 91655, exact-pad scheduler | Set class 1, fan 5 target in 50-RPM units |
| `0F/80` | 65430, 9069 | Read five-byte region records; admit the reviewed 18-LED geometry |
| `0F/84`, `0F/04` | 65430, 9069, 85191 | Read actual first-region brightness; write profile 1, region 0; preserve raw 0..255 baseline |
| `0F/82`, `0F/02` | 65430, 79151, 9069, 85191 | Read and set effect, flags, rate, color count and RGB values |
| `0F/03` | 65430, 9069 | Stream profile 0 frames with region/row/start/end and bounded RGB data |
| `00/84`, `00/04` | 9069, product initialization | Read/set device mode; mirror entry uses Driver directly and saves prior mode |

Ownership is firmware `0` or Synapse `1`. Presets are Low `0`, Medium `1`, High
`2`, Quiet `3`, Balanced `4`, Performance `5`, Off `6`. Fixed control directly
requests `[1,1,target/50]`; Off requests `[0,6,0]`. A host curve preserves an
existing Smart preset or directly selects Quiet `[0,3,0]`, then sends calculated
`0D/01` demands. No preliminary Fixed, Medium, Automatic or Normal reset is
required. Source initialization enters Driver `03 00` directly; ordinary pad
cleanup in Synapse skips a blanket Normal handoff for this Chroma device.

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

## Typed cooling-demand API

Turn on **Allow apps to request more cooling** and apply it to opt in. It is off
by default. The authenticated current-user service pipe exposes explicit demand
leases only while that opt-in and a user-started Fixed or curve session are active.
Leases are bounded to eight, expire within 30 seconds, and belong to the
requesting Windows process identity. They can raise demand while a user-started
fan session is active; they do not start an inactive session. Settings, desired
lease demand, confirmed configured target and measured-speed availability remain
separate. See the typed request definitions in `OpenBlade.Contracts`.

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
This implementation supersedes the stale capture-only admission design in draft
PRs #125 and #621; historical physical results retain their original scope.
