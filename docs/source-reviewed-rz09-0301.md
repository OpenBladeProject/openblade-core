# Blade 15 Advanced (Mid 2019), RZ09-0301

OpenBlade supports Balanced and Custom performance on this model, with CPU and
GPU Low, Medium and High levels. Balanced also supports fixed fan targets and
host fan curves on full-power AC. Keyboard brightness and logo Off, Static and
Breathing use separate controls; the keyboard effect stays unchanged.

## Exact identity and source review

The [original admission](../evidence/source-reviewed-blades/2026-11-07-rz09-0301.json)
and [additional control review](../evidence/source-reviewed-blades/2026-11-07-rz09-0301-controls.json)
pin the exact Synapse product 581 modules, SHA-256 values and UTF-8 excerpts.
Razer's [model page](https://mysupport.razer.com/app/answers/detail/a_id/1580)
identifies the Mid 2019 Advanced as RZ09-0301. An original
[owner report](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/199) identifies
USB `1532:0245`, and Razer's product 581 manifest names that PID's separate
driver carrier and both ordinary Advanced and Mercury White editions.
The copied 2020 guide link in the middleware is not used as identity evidence.

The owner report in [issue #758](https://github.com/OpenBladeProject/openblade-core/issues/758)
records the literal Windows product name `Blade`, BIOS 1.05 and EC 1.01. Its old
cached report does not establish HID enumeration or both registry identities.
Runtime admission requires fresh Razer manufacturer and exact `Blade` product
values in both registry sources, plus the exact `1532:0245` interface and report
geometry. SKU remains diagnostic; firmware versions are provenance rather than
runtime requirements. Physical validation remains pending.

## Performance and fans

The model uses 90-byte protocol reports, interface 2, native report ID 0,
91-byte feature-report buffers and transaction IDs 1-31. Preset getters/setters
are `0D/82` and `0D/02`, with profile 1 and CPU/GPU selectors 1 and 2.
Balanced is raw 0 and Custom is raw 4. Custom getters/setters are `0D/87` and
`0D/07 [1, selector, level]`. Both CPU and GPU admit levels 0-2, including all
nine combinations. The GPU source calls its field "wattage", but the actual UI
forwards these levels unchanged; they are not claimed watt values. Native GPU
clock changes through BladeNative remain a separate vendor operation.

The source-derived Balanced manual envelope is 3,700-5,300 RPM in 100-RPM
steps. A fresh `0D/83` class-0, 12-byte metadata reply reports the current
preset's minimum, default and maximum at bytes 6, 8 and 10, in hundreds of RPM.
Every target operation uses the intersection of that current range and the
reviewed envelope. Before the first successful range query the UI uses the
source envelope.
Confirmed intersections are shared across ports and published to service/UI
capabilities, so curve fail-safe calculations use the actual confirmed maximum.
Every write still rechecks firmware; saved intent retains the source envelope
rather than being erased when a current range narrows.

Targets use `0D/81 [1, zone, 0]` and `0D/01 [1, zone, RPM/100]` for zones
1 and 2. The source UI offers one scalar target for both fans. OpenBlade reads
both registers independently and rechecks dynamic metadata before every target
setter and paired target readback. Fresh paired preset/manual flags are checked
after metadata and before each setter; drift stops further writes. Range
discovery never changes presets or
fan mode. Curve entry uses the freshly admitted maximum; the existing service
then owns target updates. Without a trusted CPU temperature input, the curve
holds its maximum-speed fail-safe rather than varying from an invented reading.

Manual control is admitted only in Balanced on full-power AC. Custom requires
Automatic fans because the exact product omits Custom manual support. Automatic
handback preserves the current preset, including on battery, and requires no
target-range or target-register read. Fixed-to-Curve and
Curve-to-Fixed changes within a confirmed admitted preset go directly through
the shared target path, without Automatic or Balanced reset writes.

## Independent brightness and logo controls

Keyboard brightness uses active-bank `03/83 [0,5,0]` and `03/03 [0,5,raw]`,
established by the later exact MCU review. The exact
vendor conversion rounds percent-to-raw upward and reports `floor(raw*100/255)`.
A change reads its prior raw value, applies one bounded value and confirms the
raw result with a separate getter. It sends no keyboard-effect or Driver-mode
command. No firmware-native brightness-step size is admitted.

Logo control uses profile 0 and LED 4: state `03/80` / `03/00`, effect
`03/82` / `03/02`. Static is effect 0 and Breathing is effect 2. The effect
precedes an On state, matching the exact vendor path; Off changes only state.
Present SET response bytes must match the submitted arguments, and independent
getters confirm the result. A status-only acknowledgment is not state readback.

Independent profile intent and observations are kept separately from keyboard
effect settings. Unknown effect/RGB state remains unknown. Profiles with no
explicit independent intent do not acquire new brightness or logo writes. A field
edit stores merged profile intent but sends and confirms only the edited fields;
unrelated readings are preserved.

## MCU effect controls

The [MCU-first lighting pass](source-reviewed-lighting.md) reconstructs an exact
PID 0245 keyboard image from the official RZ09-03017/03018 updater's resources.
Its active-bank `0F/82` getter, legacy `03/0A` setters and actual color consumers
establish Off, Static, Spectrum, random/single/dual-color Breathing and both Wave
directions. The legacy setter copies the saved LED 5 record into active state,
including brightness, so effect confirmation is followed by a fresh active
brightness read and the requested brightness target when necessary.

These controls coexist with independent field editing. Old unmanaged profiles
remain unmanaged; explicitly selecting an effect records separate keyboard-effect
intent. That intent is not a firmware observation. The MCU establishes 6×16 row
bounds, but no custom-frame backend or input-to-light map is admitted here.

## Remaining source limits and verification

Custom mode 8 is observed without pixel contents. A new typed built-in effect
can replace it directly, but it cannot supply a framebuffer restoration baseline.
Host-frame ownership, lifecycle and exact restoration retain their separate
implementation limits.

Charge limits and temporary full charge remain unadmitted: product 581 does not
enable the shared battery-optimizer path. Shared helper presence does not prove
model applicability. Maximum Fans, CPU Boost, HyperBoost, native GPU clocks,
keyboard drivers and THX integrations retain their separate evidence limits.
The [catalog review](https://github.com/OpenBladeProject/blade-driver-catalog/pull/29)
records explicit decisions for all seven existing driver artifacts.

Software verification covers exact identity, dynamic bounds, AC/DC admission,
direct transitions, independent readback, malformed responses, partial and
ambiguous submission, and recovery without setter replay. Physical fan speed,
lighting response, charging behavior and lifecycle restoration remain untested
on this model. See the [support guide](experimental-blade-support.md) for reports.
