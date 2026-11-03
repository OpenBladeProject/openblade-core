# Offline helper measurements, 2026-11-03

Release, .NET 10.0.12, Windows build 26300, x64. One warmup set and three measured
rounds completed all 27 phases and semantic checks. The measured production source
was unchanged from `295d7e6d`; the benchmark was added in the working tree. The
[sanitized report](results/2026-11-03-measurements.json) records source/assembly
hashes, module identities, raw measurements and the original report hash.

| Workload | Concurrency | Observed wall-time range |
| --- | ---: | ---: |
| 100,000 status publications, stalled subscribers | 1 | 6.23-17.10 ms |
| Same | 4 | 12.47-19.34 ms |
| Same | 8 | 18.29-34.29 ms |
| Fake GPU sampling, three reads and two 150 ms waits | 1 / 16 / 64 | 310.89-326.11 ms |
| Cancellation to observing every caller complete | 1 | 0.05-0.06 ms |
| Same cancellation interval | 16 | 0.12-0.19 ms |
| Same cancellation interval | 64 | 0.38-0.60 ms |

The hub delivered only each subscriber's latest revision, retained capacity one,
rejected subscriber nine and restored capacity after disposal. Every fake sampler
performed exactly three reads/two waits; cancellation performed one read/one wait
and no later read. Fanout allocated about 30.4 MB per 100,000 publications,
including one fixture snapshot record per revision. Whole-phase GPU cancellation
time includes setup and diagnostics; the table uses the separately measured
cancellation-to-completion interval.

These results establish bounded helper behavior under synthetic load. Timings
varied materially from earlier same-day runs under uncontrolled host load, so they
are observations rather than speedup claims or CI thresholds. This does not measure
the complete Service/Session Agent, real GPU counters, installed idle power or
hardware latency. Managed retention includes accumulated report objects; resource
snapshots do not establish peak consumption or multi-hour stability.

Two Claude reviews and a separate Codex review led to bounded output-drain and
cancellation cleanup improvements in the harness. A Debug build compiles but
execution intentionally refuses measurement. The final Release run includes those
harness corrections; no additional production-helper change was justified by it.
