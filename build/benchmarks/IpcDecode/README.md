# IPC decode benchmark

Run from the repository root:

```powershell
dotnet run --project build/benchmarks/IpcDecode/IpcDecode.csproj -c Release -p:NuGetAudit=false
```

This opt-in benchmark decodes deterministic profile-request and status-event
frames through the production codec. It uses memory streams and synthetic
data, opens no pipes, and performs no hardware or application operations.
Frames are encoded once, then decoded after 2,000 warmups over five rounds of
10,000 iterations each. Every iteration verifies the decoded values. Output
reports median time and allocation per decode. The project is outside the
solution so timing is never a CI pass/fail threshold.

Compare runs on the same runtime, machine and build configuration. These
measurements describe decoding these frames, not whole-application latency.

On the same Windows host with .NET 10 Release builds (2026-11-03), the
document-based validator at `a6bc903e` and the shared streaming validator gave:

| Frame | Encoded bytes | Before, median microseconds | After, median microseconds |
| --- | ---: | ---: | ---: |
| Profile request | 1,882 | 27.89 | 23.77 |
| Status event | 4,895 | 62.45 | 51.92 |

That is approximately 15% and 17% less decode time for these fixtures.
Allocations were effectively unchanged (about 19 KB and 43 KB per decode).
