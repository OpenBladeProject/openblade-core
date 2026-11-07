# Blade 15 Advanced (Mid 2019), RZ09-0301

OpenBlade supports Balanced and Custom performance on this model, with CPU and
GPU Low, Medium and High levels. Automatic fan handback preserves the current
preset. Performance changes require a full-power adapter.

## Source review

The [admission manifest](../evidence/source-reviewed-blades/2026-11-07-rz09-0301.json)
pins the exact Synapse product 581 modules, SHA-256 values and byte-offset evidence.
Razer's [model page](https://mysupport.razer.com/app/answers/detail/a_id/1580)
identifies the Mid 2019 Advanced as RZ09-0301. An original
[Mid 2019 Advanced owner report](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/199)
identifies USB `1532:0245`, and Razer's product 581 manifest names that PID's
separate driver carrier and both ordinary Advanced and Mercury White editions.
The copied 2020 guide link in the middleware is not used to identify this model.

The owner report in [issue #758](https://github.com/OpenBladeProject/openblade-core/issues/758)
records the literal Windows product name `Blade`, BIOS 1.05 and EC 1.01. Its old
cached report does not establish HID enumeration or both registry identities.
Runtime admission therefore requires fresh Razer manufacturer and exact `Blade`
product values in both registry sources, plus the exact `1532:0245` interface and
report geometry. Another laptop with a generic `Blade` name does not pass the
HID identity check. SKU remains diagnostic; the reported firmware versions are
provenance rather than version requirements.

The model uses 90-byte protocol reports, interface 2, native report ID 0,
91-byte feature-report buffers and transaction IDs 1-31. Preset getters/setters
are `0D/82` and `0D/02`, with profile 1 and CPU/GPU selectors 1 and 2.
Balanced is raw 0 and Custom is raw 4. Custom getters/setters are `0D/87` and
`0D/07 [1, selector, level]`. Both CPU and GPU admit levels 0-2, including all
nine combinations. The GPU source calls the field "wattage", but the actual
product UI forwards these levels unchanged; they are not claimed watt values.
Native GPU clock changes through BladeNative are a separate vendor operation.

Every setter retains fresh identity, competing-controller, session and power
admission, independent readback and shared write-pause recovery. Unknown or
contradictory adapter readings block thermal setters. An ambiguous submitted
write pauses control and recovery reads state without replaying the setter.
Automatic handback changes the fan flag directly rather than resetting presets.

## Available controls and remaining tests

Fixed fan speeds and curves require further review of this model's dynamic fan
ranges. Lighting, charge limits, Maximum Fans, CPU Boost, HyperBoost and driver
or THX integrations are not enabled by this profile. The driver catalog records
an explicit review of all seven existing artifacts, without inheriting another
model's driver stack. Windows battery-health information remains independent of
a firmware charge-limit command.

Software verification covers exact identity rejection, Custom command bytes and
all nine level pairs, Automatic handback, power failures, independent readback,
ambiguous submissions and recovery. Physical operation, restoration and
sleep/resume on this model remain untested; source review does not change that
status. Use the [support guide](experimental-blade-support.md) when reporting
results.
