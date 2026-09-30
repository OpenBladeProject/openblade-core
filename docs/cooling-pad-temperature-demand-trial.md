# Bounded attended temperature-demand trial

This developer experiment connects real host temperature readings to configured
pad demand. It extends the completed 2200-demand experiment with a finite
sampling interval. It does not enable an installed service loop or establish
measured RPM, autonomous firmware regulation or production admission.

The trial policy uses CPU points (40 C, 1000 RPM) and (90 C, 2200 RPM), and GPU
points (30 C, 1000 RPM) and (80 C, 2200 RPM). Endpoints clamp to 1000..2200 RPM.
Each curve is interpolated independently, rounded upward to 50 RPM, then the
larger demand is chosen. This illustrative policy is not Synapse's preset.
An explicitly inactive GPU uses CPU alone. Missing or invalid CPU, missing
active GPU, or acquisition age at least five seconds blocks further writes.
Acquisition age includes read duration; sensor-origin timestamps are absent.

## Attended sequence

```text
OpenBlade.Capture validate-rz09-0528-cooling-pad-temperature-demand-trial --confirm-target RZ09-05289EN4:1532:0F43:0200:TemperatureDemand1000-2200-Medium-PreferredQuiet --confirm-recovery ManualPowerCycleOnly
```

1. Keep USB and 12 V connected, HyperBoost inactive, and Synapse/Razer services
   stopped. Confirm quiet normal fan behavior and full-brightness static-green
   lighting, without flashing red or sleep indication.
2. Pass exact model/SKU, pad/interface and competing-controller admission.
   BIOS versions are provenance only. Obtain valid telemetry before opening
   the actor. Query raw `0000`, acquire `0300` and query it.
3. Refresh telemetry, submit the captured vendor `000600` command immediately
   followed by the typed demand, with no operator pause between them. Recheck
   sample age immediately before native submission, after controller guards.
   Confirm normal fan/green behavior. Physical-check pauses hold the last target;
   obtain fresh telemetry after the first-demand pause before planning updates.
4. Observe ten further samples, two seconds between reads. Submit only changed
   configured demands. No workload is started automatically. An operator may
   use an ordinary foreground task during this interval. Stable temperatures
   may produce no demand changes and cannot establish changing-temperature
   response. Confirm normal physical behavior after the finite interval.
5. Reassert captured Medium `01012C` and confirm normal behavior. Submit
   captured `000600` and confirm return to the preferred quiet baseline.
   Restore/query the initial raw `0000`, then confirm quiet normal green
   behavior. These observations do not turn raw control state into fan readback.

Any ambiguity, failed echo, cancellation, bad telemetry or refused physical
check stops later firmware writes. There are no retries or firmware writes in
cleanup. Native/telemetry disposal is bounded; late work cannot resume control.
Manual recovery may be required and power cycling does not guarantee quietness.
Do not run USBPcap concurrently or restart Synapse/services for this experiment.

The result must separate telemetry, planned demands, skipped unchanged targets,
attempted writes, exact acknowledgements and physical observations. A demand
echo does not prove measured RPM or temperature tracking. Preserve failures as
separate results and do not infer full-range validation from this limited trial.

The preflight, refreshed initial sample and ten updates total twelve sensor reads
and eleven demand-planning samples. Nineteen distinct transaction IDs are
reserved before actor use, including all possible updates and restoration.
Bounded best-effort actor disposal does not itself establish a fully-disposed
receipt; retain that limitation when interpreting typed outcomes.

Preparation validation: 23 focused trial tests and all 777 Capture tests passed.
Capture build had zero warnings/errors; independent static source review passed.
The preparation checks above preceded the live attempt recorded below.
## Live attempt spanning 2026-09-29/30

Source `389a1db0` ran from 2026-09-30T06:51:37Z to 07:02:26Z. All twelve
sensor readings reported CPU 45 C and an explicitly inactive GPU. One configured
1150-RPM demand was acknowledged and physically reported normal; all ten further
plans were unchanged and suppressed. Thus this attempt did not exercise a
changing-temperature demand update or establish thermal tracking.

First-demand, finite-interval, Medium and quiet-restoration physical checks
passed. Original raw `0000` restoration and query also passed. The last physical
check exceeded its operator timeout: the process exited 2 with
`finalPhysicalConfirmation/OperatorTimeout`, `finalPhysicalConfirmed=false`,
`trialSequenceCompleted=false` and conservative `manualRecoveryRequired=true`.
The subsequent user reply confirmed quiet normal fan behavior, full-brightness
static-green lighting and no flashing-red/sleep indication after process exit.
Record that as a separate late observation; it does not alter the typed result
or establish that manual recovery was performed. No recovery action was taken.

The private 9535-byte JSON has SHA-256
`B150C9812B35496CAB0419920B729B1223F941E77DD9E87040CCC01C6A3CDDBF`.
Result flags and reviewed source support nine logical exchanges without an
independent native packet trace. Source disposal was confirmed by the result;
actor disposal remains bounded best-effort. No Synapse/service restart, lighting
setter, workload, retry or power cycle was performed by this trial.

Post-exit inventory found no active Razer services. A separate supervised Capture
process appeared after this trial had exited; it was not stopped or used here.
Do not claim a clean global process inventory or run another control trial while
its purpose remains unknown. This attempt supports one steady-temperature
1150-demand observation and later normal quiet behavior; production admission,
measured RPM, ownership, full range and changing-temperature response remain
unvalidated. Preserve the timeout rather than rerunning merely to obtain a
successful final flag.
