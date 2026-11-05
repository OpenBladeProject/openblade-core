# Passive Windows battery observation

`OpenBlade.Capture observe-windows-battery` records Windows battery-class queries
without contacting the OpenBlade service or accessing HID or raw EC registers.
It is a developer tool, excluded from the installed product. The firmware
explorer remains an independent, entirely offline tool.

```powershell
dotnet build src/OpenBlade.Capture/OpenBlade.Capture.csproj
./src/OpenBlade.Capture/bin/Debug/net10.0-windows10.0.26100.0/OpenBlade.Capture.exe observe-windows-battery artifacts/battery-observation.jsonl 300 5 idle-light
```

Create the output directory first. The file must not exist. Arguments after the
path are the sample-admission window in seconds (1–900, default 300), the wait
between queries (1–60, default 5), and `idle-light` or `uncontrolled` (default).
The workload is an operator label. `1 5 idle-light` admits at most one query.
The command changes no profile or fan setting and cannot confirm Silent mode
or RPM. Keep recordings private unless reviewed and sanitized.

Each observation uses a separate child process for synchronous Windows calls.
After process creation, its query/output wait has an eight-second deadline;
termination confirmation has a further two-second deadline. A timeout stops
recording, including when termination cannot be confirmed. Process creation
and output-file I/O are outside that deadline. Queries never overlap or catch
up in bursts. An admitted query may finish after the configured window.

The JSONL preserves UTC query bounds, monotonic elapsed time, native errors,
returned byte counts and explicit availability. Query time is not a sensor
sample timestamp. Battery temperature is optional: `ERROR_INVALID_FUNCTION`
means unsupported, not zero degrees. Celsius is `raw / 10 - 273.15`; unknown
status sentinels remain null, while a valid zero rate remains zero. Capacity
and signed rate include the driver's relative-units flag. Device paths,
serials and unique identifiers are excluded; ordinals are not stable identities
across queries. Enumeration is capped at 16 interfaces.

The recording stops at the first observation without a usable temperature.
Exit codes are 0 for a completed window, 3 for an observation stop, 2 for bad
arguments, 1 for a file/process error, and 130 for cancellation. Cancellation
can leave complete JSONL records without an end record. The internal worker
argument is an implementation detail; invoke the bounded parent command.

Native contracts: [battery enumeration](https://learn.microsoft.com/en-us/windows/win32/power/enumerating-battery-devices),
[information query](https://learn.microsoft.com/en-us/windows/win32/power/battery-query-information-str),
and [status](https://learn.microsoft.com/en-us/windows/win32/power/battery-status-str).

## Why the recording excludes service telemetry

The current interactive `GetStatus` and `SubscribeStatus` handlers can activate
user configuration after a cache miss or deferred recovery. That can perform
hardware work. Protocol-51 compatibility avoids activation but acquires a
lease that closes thermal admission and changes pending control state.
Neither route is an inert observer for this experiment. `ObserveState` also
reconciles hardware. No service request is issued by this command.

The current `LiveReadings` contract also lacks per-reading timestamps. Repeated
cached RPM or temperatures cannot be counted as fresh samples, and a status
revision is not their acquisition time. A future fan comparison needs a
genuinely passive telemetry source with clear freshness. Existing recordings
can be analyzed offline without adding polling or control work.

## RZ09-0581 observation on 2026-11-05

The bounded host query identified one system battery. Temperature returned
`ERROR_INVALID_FUNCTION` (1), zero returned bytes, and no temperature value.
The status query succeeded with 16 bytes: AC-online and charging flags, a
positive 35,210 mW battery charge rate and 16,166 mV. This is battery-side flow,
not adapter input power. The operator labeled the workload idle/light desktop;
no CPU workload measurement or fan RPM was collected.

The initial sandbox attempt was denied at `OpenBattery` (error 5) and never
queried temperature. The subsequent host-access query above distinguishes that
permission failure from the driver's unsupported-temperature result. No longer
recording followed, and no settings required restoration.

This result does not refute the firmware's strongly inferred
[battery-temperature channel](so6120-silent-automatic-policy.md#input-provenance-and-the-battery-temperature-interpretation).
It means this Windows interface cannot expose that channel here. All seven
live comparator inputs, the active table and physical zero/restart behavior
remain unmeasured. The Windows reader is model-independent; the SO6120 register
interpretation and this physical result remain exact-image/device scoped.
