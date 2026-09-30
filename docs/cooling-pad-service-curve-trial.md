# Finite attended service-coordinator curve trial

This developer Capture command exercises the same `CoolingPadControlRuntime`
and typed `CoolingPadProtocolPortFactory` used by the service composition. The
installed service's default pad factory remains closed. The command does not
use service IPC, start workloads, restart services, or invoke Synapse. This
document specifies the trial and records its bounded attended result below.

This exercises the core coordinator, planner and Protocol adapter. It does not
exercise the outer `ServiceControlRuntime` admission for low power, active user
session, confirmed laptop HyperBoost state, or service IPC. HyperBoost must
remain inactive throughout, and each physical/phase prompt repeats that operator
requirement. The trial cannot grant production admission or validate installed
service control.

Use a reviewed build on the exact RZ09-05289EN4 host and 1532:0F43 rev0200 pad:

```powershell
OpenBlade.Capture.exe validate-rz09-0528-cooling-pad-service-curve --confirm-target RZ09-05289EN4:1532:0F43:0200:ServiceCurve1000-2200-Medium-PreferredQuiet --confirm-recovery ManualPowerCycleOnly
```

Exact arguments are checked before metadata access or construction of the
temperature source or native factory. A fresh operator confirmation must report
preferred quiet behavior, normal green lighting, no red/sleep indication, and
HyperBoost inactive. HyperBoost's inactive state is operator provenance here.
Each operator wait is finite, at most ten minutes. Any response other than
`YES`, timeout, or cancellation stops subsequent writes. Manual power cycling
is the attended contingency; it does not guarantee quiet behavior.

The operator starts their chosen workload and confirms readiness. Only then
does the harness construct its disposable temperature source and coordinator.
The strict Protocol factory enforces exact host/SKU, pad/interface/report
geometry and competing-controller checks. BIOS is provenance, never admission.
The coordinator requires raw0000, acquires raw0300 and verifies it, then sends
the captured entry000600 and first typed configured demand without a human
pause between them. The factory and coordinator retain their existing bounded
native operations and last-moment submission admission checks.

The illustrative OpenBlade policy is CPU (40°C,1000), (90°C,2200) and GPU
(30°C,1000), (80°C,2200), with clamped endpoints, linear interpolation, upward
50-RPM quantization, and the larger configured demand. These are not Synapse
presets or recommended production settings. CPU readings are EC/TZRZ readings,
not CPU-core temperature; active GPU readings come from NVML. The current
Windows GPU inactivity heuristic is not trustworthy evidence of inactivity
for this trial: missing or inactive GPU telemetry pauses it instead of silently
using CPU alone. Invalid or at-least-five-second-old telemetry also pauses.
The source-origin timestamp is unavailable; host acquisition age is bounded.

After the first demand, the operator confirms normal fan/lighting behavior.
Thirty warm maintenance ticks follow, separated by two-second delays. The
operator then stops or reduces the workload and confirms readiness; thirty cool
ticks follow. Each tick takes fresh telemetry and suppresses a demand only if
it equals the last exactly acknowledged demand in this session. The operator
then confirms normal behavior across the intervals. Human waits hold the last
demand and suspend sampling; this is not continuous thermal regulation through
those waits. Workload timing is operator context, not a performance trace.

Only a still-running, unambiguous session can proceed to explicit restoration.
The same coordinator retains the same port and serializes Medium01012C, a
physical Medium confirmation, quiet000600, a physical preferred-quiet
confirmation, and saved raw0000 restoration plus its query. A final physical
quiet/green confirmation completes the trial. A failed stage stops all later
writes, including restoration. No reopen, retry, automatic recovery, or
firmware write occurs in `finally`; only resources are released. Restoration
is internal developer functionality and is absent from service IPC.

JSON distinguishes requested/planned/acknowledged demand and physical checks.
`confirmedStages` covers the runner's workload and interval gates; the command's
`initialConfirmed` and the separate `restoration` physical booleans cover their
own gates. `readings` includes preflight and first-demand reads as well as the
60 maintenance reads. Read duration is measured around the source call; it is
not the exact age at the native submission boundary. The coordinator checks
age again at that boundary.

`operations` records typed service-port results. The complete logical exchange
count is source-inferred only for a fully completed, unambiguous trial with
completed cleanup. Acquisition includes its internal raw0300 query, and the
coordinator separately queries raw0300 again. Partial results retain completed
operation evidence but do not claim a complete exchange count. There is no
packet trace or measured RPM here. Raw0000 is not fan-state readback, and quiet
behavior proves neither firmware temperature regulation nor ownership handback.
Temperature/demand changes alone do not prove thermal tracking, full-range
behavior, or production admission.

Cleanup closes the port independently of a blocked telemetry read. Source
disposal waits for its owning read; overdue cleanup is retained and cannot
resume planning or writes. `resourceCleanupCompleted` means the managed cleanup
path completed; it is not an independent proof of native `FullyDisposed`.
Any incomplete trial after construction conservatively requests manual
attention. No lifecycle or USB trial is part of this command.

## Attended result, 2026-09-30

Published source `37b51016c9276e41a834955d77e34e10c5ac6370` completed this
trial with exit zero on the exact target. The private typed result is
129,672 bytes, SHA-256
`C317FE00F1B96EB8D2DAAC5E27D7CDAE784643A3528FFA6E57BB50C60D14D237`.
It records 62 temperature readings, 30 warm and 30 cool maintenance decisions,
14 acknowledged configured demands and 47 unchanged-demand suppressions.
CPU EC/TZRZ remained 45°C; active GPU NVML readings ranged from 50°C to 75°C,
starting at 68°C and ending at 50°C. The demand sequence was
1900, 2100, 1950, 1900, 1850, 1800, 1750, 1700, 1650, 1600, 1550, 1500,
1550 and 1500 RPM. These are configured demands, not measured fan RPM.

The operator confirmed the initial physical state and all five subsequent
physical gates. Medium, preferred quiet and original raw0000 restoration/query
were acknowledged; their physical confirmations passed. Cleanup completed,
the source was disposed, submission was known and no manual recovery was
required. The final historical demand acknowledgement remains 1500; it does
not describe the fan after the separate preferred-quiet command.

User-authorized Computer Use started the operator-configured windowed Steel
Nomad run. The warm gate was released after its rendering window appeared;
the cool gate was released after both UI inventory and process inspection
showed the rendering process absent. Workload completion could precede the
end of the nominal warm interval. The workload context is not a timed load
trace or proof of sustained warming. No benchmark result was uploaded.

The 20 recorded typed operations imply 23 successful logical exchanges from
reviewed source, including internal acquisition and restoration queries.
There is no independent packet trace. No lighting setter, Synapse or Razer
service restart, USB change, power cycle or automatic recovery occurred.
Post-exit inspection found no Capture controller process.

An earlier attempt on the same source stopped at initial telemetry admission
with unavailable GPU telemetry and no typed pad operations. It remains a
separate failure. Subsequent exact-build counter diagnostics and finite
temperature-only previews succeeded, including ten windowed workload samples;
they do not establish the precise cause of that earlier missing sample.

This result supports finite host-planned demand changes and attended
restoration through the core coordinator/adapter. Outer installed-service
admission, continuous transaction management, measured RPM, thermal causality,
firmware Auto regulation, full configured range and production control remain
unproven. Lighting support remains separate outstanding work.
