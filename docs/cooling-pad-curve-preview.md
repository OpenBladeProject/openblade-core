# Cooling-pad curve planning preview

The pure planner evaluates separate CPU and GPU curves, chooses the larger
configured demand, clamps endpoints and rounds interpolation upward to 50 RPM.
CPU temperature is required. An explicitly inactive GPU uses CPU alone; an
active or unknown GPU with no valid reading blocks planning. Missing, invalid,
stale or stopped input produces no demand, not zero or firmware restoration.

This is an explicit OpenBlade policy, not a reconstruction of Synapse's preset.
The finite developer preview uses illustrative points:

| CPU temperature | Demand | GPU temperature | Demand |
| --- | --- | --- | --- |
| 40 C | 1000 RPM | 30 C | 1000 RPM |
| 90 C | 2200 RPM | 80 C | 2200 RPM |
| 100 C | 3200 RPM | 90 C | 3200 RPM |

These configured targets are not measured RPM or production recommendations.
Only the separate attended 2200-demand trial established a physical response.

```text
OpenBlade.Capture preview-rz09-0528-cooling-pad-curve --confirm-target RZ09-05289EN4:ReadOnlyTemperature-CoolingPadCurvePlanning --samples 5
```

The preview checks exact host metadata before opening existing Windows CPU/NVML
telemetry. BIOS versions are provenance only. It constructs no HID/pad transport
and sends no pad reports. It permits 1..30 readings, waits two seconds between
readings and rejects acquisition age at or above five seconds. The monotonic
age includes read duration; sensor-origin timestamps are unavailable. Timed-out
reads cannot resume sampling, and source disposal waits for an owning read.

## Completed read-only observation

Developer source `c8ba8784` completed five readings with exit zero on 2026-09-29.
CPU was 45 C throughout; GPU was explicitly inactive. Each planned demand was
1150 RPM, with host acquisition durations 155.13..169.29 ms. The temperature
source was disposed. No fan commands, workload, Synapse or service actions
occurred. The private JSON has SHA-256
`38142D30982B9E588AA36BD12EC8DB8E719ACD0DF3439281CDFF2338623C2983`.

Validation: planner 17 focused tests passed; Service 1978/1979 passed under the
sandbox, with the sole named-pipe ACL failure passing on a host rerun. Preview
14 focused tests and all 754 Capture tests passed. Exact committed-source build
had zero warnings/errors, and independent source review passed.

This establishes real telemetry acquisition and planning, not actual pad
temperature regulation. Production admission stays closed. The next control
step requires a reviewed, bounded attended trial connecting changing demand to
the already tested acquisition/restoration sequence; planned values alone do
not authorize writes or establish physical validation of the configured range.
