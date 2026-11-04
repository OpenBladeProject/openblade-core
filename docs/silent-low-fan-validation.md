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
required. The 2,000 RPM preparation isolates the experiment's physical effect;
it is not a production prerequisite.

After a clear first round trip, `validate-silent-lowest-spinning` can descend
through 1,900, 1,800, 1,700, 1,600 and 1,500 RPM. Each target must be independently
retained. Positive tach readings must remain stable after at least 20 seconds;
early positive samples could still be spin-down momentum. The first confirmed
fan stop, clamp, unstable result or unsafe condition ends further candidates.
The result bounds the lowest **observed spinning target under those conditions**,
not a universal hardware minimum or guaranteed continuous speed.

Neither operation sends zero or 100 RPM. A separate, developer-only
`validate-silent-literal-zero` scenario is prepared for one literal target-byte
`00` experiment. The source wrapper encodes zero without a special case; its
firmware meaning remains unknown. This scenario requires separate explicit
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
