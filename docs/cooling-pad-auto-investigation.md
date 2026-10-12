# Cooling-pad Auto and host-managed curves

Investigated 2026-09-29 Pacific for issue #119. This is read-only synthesis of
existing August and September evidence, installed Synapse module source, and
Razer's published guide. No device
reports, workload, Synapse launch, service action or capture was performed.

## Conclusion

The August record supports a Synapse-hosted Smart Fan Curve: laptop CPU/GPU
telemetry is evaluated on the host and calculated fan demand is sent to the pad.
The default Auto UI selection and Smart Fan Curve are distinct observed actions.
The available evidence does not establish that default Auto uses this same loop,
or that the pad autonomously follows laptop temperature without a host controller.

Our September phrase "quiet Auto restored" means the captured Auto selection
was acknowledged and the operator observed the preferred quiet fan behavior.
It does not mean temperature-responsive operation was validated. With Synapse
and its services stopped, no OpenBlade pad temperature-control loop is running.
Do not present this observed quiet state as active temperature regulation.

## Evidence and limits

The August [protocol observation summary](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/decoded/rz09-0581-pid-02e0-bios-4.00-cooling-pad-protocol-observations.json)
records host evaluation using CPU, GPU or both; approximately two-second upstream
telemetry; and suppression when the scaled target has not changed. It records
`0D/01` dynamic demand with fields `01 05 floor(target / 50)`, configured values
500 through 3200 in steps of 50. Thus 2200 maps to `01 05 2C`. This is target
demand, not measured fan RPM or laptop temperature transmitted directly.

The archived middleware summary describes step selection, not linear
interpolation: choose the configured point at the smallest temperature strictly
greater than rounded current temperature; for both sources, choose the greater
selected target. Equality advances to the next point. It also records zero when
no greater point exists. These are historical source-derived observations,
identified by hashes of vendor middleware bodies. The original bodies were not
located and re-inspected during this investigation. Do not copy that zero edge
into a production thermal policy or treat the configured range as hardware-validated.
Two-second telemetry does not imply a write or required heartbeat every two seconds.

The August [Smart Fan Curve capture](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/annotations/2026-08-09-rz09-0581-cooling-pad-smart-curve.json)
records CPU/GPU curve points and small calculated target messages, with no
observed curve-table upload. This corroborates host evaluation. The
[full-root capture](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/annotations/2026-08-09-rz09-0581-hyperboost-full-root-transport.json)
and protocol summary record five acknowledged dynamic targets with scaled bytes
`14`, `1C` and `20`. The
[one-shot host-demand trial](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/annotations/2026-08-09-rz09-0581-cooling-pad-host-control-physical-positive.json)
physically confirmed one Auto-state-plus-2200-demand sequence on RZ09-0581;
recovery used Synapse. It did not validate a sustained loop or the whole range.

The August [UI-transition capture](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/annotations/2026-08-09-rz09-0581-cooling-pad-auto-fixed-oracle.json)
records Smart Fan Curve and Fixed 2200 each producing `0D/10:01022C`, while
returning to Auto produced `0D/10:000600`. A single mode-selection payload
therefore cannot identify the host's continuing curve policy. Default Auto
must not be equated to custom Smart Fan Curve solely from its name.

The September [vendor Auto round trip](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/annotations/2026-09-29-rz09-0528-cooling-pad-vendor-auto-round-trip.json)
contains no `0D/01` demand writes during the 44.86975-second Auto interval.
This does not establish onboard temperature regulation or refute a host loop
that suppresses unchanged targets. No correlated changing-temperature/load
experiment was performed. The physically successful
[preferred-Auto recovery](https://github.com/OpenBladeProject/openblade-captures/blob/chore/rz090528-pad-vendor-evidence/annotations/2026-09-29-rz09-0528-cooling-pad-preferred-auto-recovery.json)
sent no demand updates and tested quiet behavior, not thermal tracking.
Raw control values `0000`/`0300` and opaque getters remain separate from fan state.

Razer's [master guide](https://dl.razerzone.com/master-guides/RazerSynapse4/LaptopCoolingPad-00003907-en.pdf),
pages 10, 11 and 17, places additional smart settings in Synapse and describes
Smart Fan Curve as responding to CPU/GPU temperatures. This agrees with the
host-managed interpretation; it does not disclose the default Auto algorithm,
firmware sensor capabilities, source arbitration or failure behavior.

## Installed module source check

A bounded read-only search found the installed cached pad middleware module
`6259.9704435af82c7937e70b.js`. Its containing cache file has SHA-256
`F9E05E2C6C75E415452580892C1248B5359EBA259BE9C1426A8BAA3D9B4C7EB9`.
This hash identifies the entire local cache file, not an extracted JavaScript
response body; no cache file, private path or vendor source is committed.
This is a fresh source inspection, separate from the historical August hashes.
Independent review confirmed setter, encoder and ordinary loop at zero-based
UTF-8-decoded text character offsets 1815747, 709876 and 1860401 respectively;
these are not file byte offsets.

The pad setter's disabled-controller branch calls
`setThermalFanCoolingMode(6, 0, 0)`. That matches the semantic fields of the
captured `0D/10:000600` selection. Its Fixed branch instead sends the selected
preset and scaled target. With HyperBoost inactive, the ordinary temperature
loop clears its remembered target
and returns when `fanController.isEnabled` is false, and returns for Fixed.
When enabled and non-Fixed, it evaluates configured CPU/GPU curve data and sends
scaled fan demand only when needed. The current source also corroborates the
strictly-greater-point selection, combined-source maximum and `/50` scaling. The source does not explicitly limit this
loop to the Smart enum: the recorded enum has Auto, Fixed and Smart values.
Do not replace the actual enabled/non-Fixed condition with a guessed Smart-only gate.
A separate HyperBoost branch precedes those ordinary controller gates; this
finding does not imply that disabling ordinary control stops every demand path.

The pad UI source ties the fan-control toggle to `isEnabled`, but a direct,
pad-specific default Auto label-to-disabled-state mapping was not established.
Thus the captured command selects the software controller's disabled path;
it does not start that host temperature loop. Neither the numeric mode nor
an enum name establishes a physically stopped fan, an onboard temperature
source, firmware ownership, or autonomous regulation. The recent observed quiet
fan after this command remains valid physical evidence. The precise default
Auto UI policy and the pad's firmware behavior remain unresolved.

## Consequence for OpenBlade

Preserve three separate concepts: requested policy, acknowledged target demand,
and measured/physical observation. Accessory Auto cannot inherit the laptop's
firmware Automatic semantics. A future pad automatic controller should explicitly
own fresh laptop telemetry and a host-evaluated curve, with an admitted demand
encoding and a defined stale-telemetry/stop policy. Fixed control and temperature
control are separate implementation slices. Existing typed telemetry/curve
infrastructure should be assessed before adding another scheduler.

Current `OpenBlade.Service/FanCurveControl.cs` already exposes
`IFanTemperatureSource` and `SafeWindowsFanTemperatureSource` for the admitted
CPU thermal zone and NVIDIA GPU telemetry. Its `FanCurveCalculator` produces two
laptop-fan targets using interpolation and laptop emergency thresholds. Reuse
of the telemetry interface is a plausible integration point; copying that
calculator, RPM limits or firmware Automatic semantics into the accessory would
mix separate contracts. The pad needs its own typed single-target policy and
admitted demand path, not another laptop control implementation.

The immediate work is offline review of the existing host-demand contract and
its fit with current OpenBlade telemetry. Default Auto's exact algorithm remains
unresolved. Any eventual hardware test must correlate changing CPU/GPU temperature
with observed demand while the operator is present; repeating quiet-state,
reconnect or mode-readback trials would not resolve this question. No further
hardware or lifecycle trial is added here. BIOS is provenance only; exact
model/SKU and pad identity remain the admission scope. August RZ09-0581 evidence
does not by itself admit a RZ09-0528 demand loop. Production admission is unchanged.
