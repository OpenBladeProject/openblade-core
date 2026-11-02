# Telemetry projection benchmark

This console program measures the actual Core history and viewport projection
methods. It uses deterministic synthetic data and never contacts the service or
hardware. It is an opt-in developer benchmark, outside the product solution.

From the repository root:

```powershell
dotnet run --project build/benchmarks/TelemetryProjection/TelemetryBenchmark.csproj -c Release -p:NuGetAudit=false -- result.json
```

Each scenario builds a retained history snapshot and projects its visible range.
Histories contain 3,600 or 86,400 samples; restricted ranges select the final
600 seconds. Data includes signed battery readings, missing readings, observation
gaps, and profile/power changes. Eight warmups precede five rounds of ten
operations. Allocation counts exclude serialization and explicit collections
between rounds. The SHA-256 digest covers the complete serialized viewport
output, including raw samples, points, statistics and events.

The initial November 2026 optimization compared `1c54ccce` with the pooled
projector, before the gap-saturation correctness fix, using .NET 10.0.12 on the
same machine. At that stage all four output digests matched. Median bytes
allocated per operation were:

| Samples | Viewport | Before | After |
| ---: | --- | ---: | ---: |
| 3,600 | Full | 4,298,944 | 670,025 |
| 3,600 | Restricted | 2,466,196 | 503,689 |
| 86,400 | Full | 99,177,311 | 2,260,105 |
| 86,400 | Restricted | 50,452,089 | 1,298,569 |

These are warmed steady-state allocations. The first pool rental retains
reusable scratch storage. Pool retention and machine load affect later runs:
the checked-in runner reproduced all four output digests, with median allocations
of 693,015 / 531,596 / 3,101,324 / 2,149,183 bytes in the same order while
other validation was active. Timing is diagnostic and machine/load dependent;
neither elapsed time nor allocation size is a CI pass threshold.

The subsequent correctness fix deliberately changes selection when missing/gap
points fill or exceed the point budget. The previous fallback could discard all
real readings while retaining only missing points. The final projector selects
representative real readings first and inserts an existing gap between samples
from disconnected segments. It preserves the first and last valid readings when
the budget permits. With two slots, disconnected endpoints cannot both be shown
without inventing continuity, so the first real reading takes precedence. Two
endpoints within one continuous run can still fit. Outer missing boundaries use
remaining space. All-missing input stays missing, and the point cap is strict.

Final output digests may therefore differ from the historical table's outputs;
that change is intentional and must not be presented as a digest-preserving
optimization. Raw samples, statistics and events retain their established
semantics and are covered by focused history/viewport tests. The runner now
reports selected valid/missing point counts per metric after the timed region,
making accidental empty plots visible in benchmark artifacts.

The final gap-fix run saved as `final-gap-fix.json` measured median allocations
of 693,015 / 531,596 / 2,670,445 / 1,933,927 bytes in the same four-case order.
The 86,400-sample full viewport now contains 300 valid points and 300 missing
points for every metric, within its 600-point budget. Its complete-output digest
changed to `63E23A82B8D0D34695401442AE13DACF95B8B36A2D785C7B3A8A8D0B0F037F1D`;
the other three viewport digests still match the original baseline. Median times
were 2.59 / 2.20 / 45.25 / 35.60 milliseconds while other validation was active.
These timings are not isolated performance comparisons with the earlier runs.

The separate native WinUI regression
`TrayXamlStartupSmokeTests.TelemetryNotificationsRenderOnceAndKeepNoSampleTimeProgression`
launches the developer harness on a private desktop. It asserts chart rebuild
counts for freshness, history, accepted sample notifications, no-sample
completion, source changes and disposal. It also captures a real rendered PNG.
That harness measures constructor allocation/time and a three-second idle
interval using a disconnected window. Those values do not measure installed
startup, live sensor latency or whole-system idle power.
