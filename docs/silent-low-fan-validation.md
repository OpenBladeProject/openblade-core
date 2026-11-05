# Silent low-fan validation

This developer-only experiment tests exact RZ09-0581 (`1532:02E0`) on confirmed
full external power. It preserves Silent while requesting targets below the
normal app range. It does not grant fan-stop capability to another model.

## Reviewed source

The three pinned Product 736 bundles in
[adapter evidence](synapse-adapter-wattage.md#pinned-vendor-files) were retrieved
as data and their complete hashes checked again on 2026-11-05. No vendor code
was executed. Independent review confirmed:

- Policy module `50779` constructs profile `1`, the selected mode, and manual
  flag `1`. Main module `3327` preserves Silent's mapping to selector `5`.
- The base device constructs `0D/02 [1, channel, mode, flag]` and
  `0D/01 [1, channel, target / 100]`. Silent manual therefore uses
  `[1, channel, 5, 1]`; 1,500 RPM uses target byte `0F`.
- Mode (`0D/82`), stored target (`0D/81`) and measured RPM (`0D/88`) are
  separate reads. An acknowledgement is not confirmation of retained state.
- The full-power path preserves the selected mode. The low external-power
  branch substitutes Balanced, so this experiment requires independently
  confirmed full adapter power.
- Silent's static advertised minimum/default/maximum is 3,400/4,100/5,200 RPM.
  Runtime fan information can override these limits. Neither source path
  establishes acceptance or physical behavior below the advertised minimum.

Recognizing `05/01` as a read-only Silent/manual observation does not authorize
new writes on other models. The new experiment is a separately admitted,
closed exact-model scenario. Existing Battery Saver trials and the failed
100 RPM record retain their original behavior and evidence.

## Bounded operations

`validate-silent-fan-stop-1500` saves two matching Silent/Automatic mode reads,
both restorable stored targets and actual fan speeds. It enters Silent/manual,
independently confirms that mode before writing targets, then establishes both
fans physically spinning at the 2,000 RPM target. Changing only the targets to
1,500 RPM tests spin-down. Three consecutive zero readings on both fans are
required. The 2,000 RPM preparation establishes a spinning starting state; it
does not by itself isolate causation and is not a production prerequisite.

After a clear first round trip, `validate-silent-lowest-spinning` can descend
through 1,900, 1,800, 1,700, 1,600 and 1,500 RPM. Each target must be independently
retained. Positive tach readings must remain stable after at least 20 seconds;
early positive samples could still be spin-down momentum. The first confirmed
fan stop, clamp, unstable result or unsafe condition ends further candidates.
The result bounds the lowest **observed spinning target under those conditions**,
not a universal hardware minimum or guaranteed continuous speed.

Neither operation sends zero or 100 RPM. A separate, developer-only
`validate-silent-literal-zero` scenario provides one literal target-byte
`00` experiment. The source wrapper encodes zero without a special case and
does not define a fan-off meaning. This scenario requires separate explicit
literal-zero approval from a present user before execution. It uses the same
spinning preparation, independent mode/target/tach reads and exact restoration;
it is not a known fan-off command or a product capability.

Every submission retains fresh exact
host/HID, controller, owner, active local session, AC/full-adapter and applicable
sensor admission. Trial admission requires CPU temperature at most 55°C and
confirmed inactive GPU activity. Sensor freshness starts before the async read.
The durable journal must flush before native submission. Restoration does not
require the trial temperature ceiling, but retains the other safety gates.

Known outcomes restore the exact saved targets and Silent/Automatic pair,
followed by independent readback. Ambiguous submission or readback stops all
further writes, including automatic restoration. Reconcile read-only before
considering a separately reviewed restoration; never replay the uncertain step.

`build/Invoke-SilentLowFanValidation.ps1` uses the existing sole-owner handoff
pattern, saves installed intent and protected hashes, and resumes the original
service and user components only after confirmed restoration or no writes.
`-Calibrate` selects the separate descending scenario. `-LiteralZero` selects
only the separately approved zero experiment; the switches cannot be combined.
Installed files are not
replaced. Hardware execution requires the user's explicit authorization and a
reviewed exact build; this document alone grants none.

## Results

The read-only preflight on 2026-11-05 confirmed plugged-in Silent/Automatic,
both fans at 2,100 RPM and a Windows CPU temperature reading of 45°C. A separate
sensor read confirmed inactive GPU activity. These observations are provenance;
fresh admission is still required at each write.

The [first Silent round trip](evidence/silent-2000-to-1500-rz09-0581-2026-11-05.json)
confirmed Silent/manual (`05/01`), both 2,000 RPM preparation targets and physical
rotation, then retained both 1,500 RPM targets. Both measured fan readings stayed
between 1,900 and 2,000 RPM in all 30 candidate samples. This did **not** establish
Silent fan stop or physical 1,500 RPM operation. All ten probe writes were
acknowledged without ambiguity. Both original 3,800 RPM targets and the exact
Silent/Automatic pair were independently restored.

The installed-state check exposed a separate wrapper defect: service restart
clears `UntilPowerSourceChange` performance overrides, so the saved Balanced
profile replaced the temporary Silent override. A separate fresh, journaled
typed request restored the exact original Silent override and lifetime. The
subsequent independent read confirmed Silent/Automatic, active admission and
equality of all ten compared intent fields. The service, tray and Session Agent
were running from their expected installed paths. The shared wrapper helper now
handles this specific missing-override case after confirmed probe restoration;
it never retries an uncertain setter or accepts unrelated intent changes.

Zero and 100 RPM were not sent in this round trip. Further results belong in
separate records rather than rewriting this negative result.

The user then separately authorized one
[literal-zero round trip](evidence/silent-literal-zero-rz09-0581-2026-11-05.json).
Both stored targets independently read back zero, and Silent/manual remained
selected. Both actual fan readings stayed between 1,900 and 2,000 RPM across all
30 samples. Zero did not act as a fan-off command in this tested condition.
The exact firmware baseline and original temporary Silent override were restored;
all ten saved intent fields and ten protected installed hashes matched.

The [nonzero calibration](evidence/silent-low-rpm-calibration-rz09-0581-2026-11-05.json)
then retained 1,900, 1,800 and 1,700 RPM targets. The measured pair settled near
1,900/2,000 RPM at each step. At the 1,700 RPM step the second fan remained
outside the predeclared ±200 RPM tracking tolerance, ending further candidates. That calibration
did not send 1,600 or 1,500 RPM. Its `lowestObservedSpinningTargetRpm: 1800`
field reflects the tolerance classification; it does not report a physical
1,800 RPM steady speed. Exact firmware and installed intent were again restored.

These observations support an approximately 2,000 RPM physical floor in
full-power Silent/manual under the sampled cool conditions. They do not prove
a universal firmware limit. Silent/Automatic had already reached 0/0 RPM at
the first trial's captured baseline; it measured 1,300/0 RPM before the zero
trial. Firmware Automatic can therefore stop idle fans in the sampled conditions.
The manual-target results do not establish an unconditional Silent minimum.
The pinned sources contain no separately established
fan-off selector. They also call an overclock-mode API separately, whose complete
implementation/readback is absent from these pins; combining another thermal
profile with a presumed Silent CPU mode would be an unsupported inference.
Consequently, Silent fan stop is not enabled by these results. Battery Saver's
earlier stop observation also lacks a sustained 2,000 RPM control and a reversal;
its causal effect remains uncertain. Production fan-stop admission is withheld
for both modes. See the [Battery Saver reassessment](battery-saver-low-fan-validation.md#withheld-production-capability).

Subsequent [static EC 1.04 analysis](so6120-ec104-fan-analysis.md) found a
normal-manual clamp consistent with these observations: Silent's factory
minimum is 2,000 RPM, while selector 3 has a separate below-minimum zero
exception. It also found Automatic zero-target stages and a distinct internal
duty override. The [MCU 1.07 follow-up](so6120-mcu107-ec-fan-bridge.md) traces
generic host register access to that path, while the normal duty API is
rejected and protection/ownership/restoration remain unresolved. These findings
supply firmware evidence for different behavior between the two modes; they
do not change the recorded trials or enable a production control.

## Automatic and custom curves

A further static inspection of the same pinned Product 736 files distinguishes
firmware Automatic from Synapse's custom curve. Policy module `50779` maps
`auto` to manual flag `0`, while both `manual` and `smartFanCurve` map to flag
`1`. In module `91058`, the Automatic monitoring branch reads the two actual fan
speeds and returns. The custom-curve branch selects targets from temperature
data and calls the same `setThermalFanSpeed(1, fan, rpm / 100)` operation.
Consequently, a custom curve does not supply a separately established way around
the observed manual RPM floor. These inspected paths expose no automatic fan-stop
temperature setter.

Further investigation is focused on Silent. Its existing Automatic idle stop
can be studied against temperature, GPU activity and power use before considering
a quieter power policy. Such a policy would encourage firmware idle stopping;
it would not establish a guaranteed off command. Silent and Battery Saver use
different thermal selectors, so common report geometry alone does not establish
identical firmware behavior. No additional probe or hardware write was made for
this source inspection.

## Online source review, 2026-11-05

The reviewed online sources do not establish a forced fan-off operation for
RZ09-0581 / `1532:02E0`. This is a bounded source finding, not proof that an
undocumented operation cannot exist. Source was inspected as text; no third-party
utility, firmware updater or additional hardware test was run.

- Razer's [Product 736 Synapse guide, page 18](https://dl.razerzone.com/master-guides/RazerSynapse4/Blade16-00000736-en.pdf)
  documents Automatic, Fixed RPM and Manual fan curve, with no separate fan-off
  setting. Its [exact-model firmware list](https://mysupport.razer.com/app/answers/detail/a_id/20854)
  lists EC 1.04; the [EC release note](https://mysupport.razer.com/app/answers/detail/a_id/20850/kw/set%20up)
  describes improved fan control without promising fan stop. The saved local
  baseline already reports EC 1.04, so this is not an untried update remedy.
- [CPUnity R-Helper at `3b5a0dc`](https://github.com/CPUnity/CPUnity-R-Helper/blob/3b5a0dce6a67eefcf15dee8afa5ec9a5773ddc74/README.md#L128-L132)
  advertises 500–3,200 RPM and Off for the **external cooling pad**. Its
  [laptop control](https://github.com/CPUnity/CPUnity-R-Helper/blob/3b5a0dce6a67eefcf15dee8afa5ec9a5773ddc74/src/ui/fan.rs#L3-L7)
  has a 2,000 RPM manual minimum; its model definitions do not establish 02E0.
- [razer-control-revived at `f600bc8`](https://github.com/encomjp/razer-control-revived/blob/f600bc86997392de27e04edefe53bf5c844bc3e2/razer_control_gui/src/daemon/device.rs#L1005-L1018)
  maps requested zero to firmware Automatic, without sending a literal zero
  target. Its tested newer-model thermal implementation is for 02C6, not 02E0.
- [razerblade-cli at `7a89806`](https://github.com/TimandXiyu/razerblade-cli/blob/7a8980671cfce1abe7f37405071509c04385fc04/razerctld.c#L221-L228)
  likewise maps a curve's zero point to Automatic. It was tested on 02B7. Its
  active profile-byte `1` requirement matches the exact-model vendor encoding
  and the already performed OpenBlade trials; it supplies no new command lead.
- [sqmagellan/razer-ctl at `158ba28`](https://github.com/sqmagellan/razer-ctl/blob/158ba283f4c7d2a5367b3b6cac95c0deaaee9642/librazer/src/descriptor.rs#L29-L47)
  records an independent older-model result on RZ09-0483 / 029F, BIOS 2.05:
  manual targets from 1,800 through literal zero remained at 2,000 RPM during
  40-second holds. That project's [02E0 entry](https://github.com/sqmagellan/razer-ctl/blob/158ba283f4c7d2a5367b3b6cac95c0deaaee9642/librazer/src/descriptor.rs#L228-L298)
  is explicitly an unmeasured generic fallback, so neither result grants this
  model a capability.
- The [original rnd-ash implementation at `016b3fe`](https://github.com/rnd-ash/razer-laptop-control/blob/016b3fe512bbfc47f38e5672533adb6df07839fc/driver/README.md#L55-L73)
  also uses zero to select Automatic. Its old-model fan-control implementation
  supplies no distinct forced-off or hardware-curve command.

The concrete supported route to idle silence remains firmware Automatic, which
already stopped this laptop's fans in the recorded Silent idle baseline. It
cannot guarantee fan stop on demand. A manual zero setpoint, a UI label of zero,
and measured zero rotation must remain separate claims. None of this research
widens production admission or authorizes another experiment.
