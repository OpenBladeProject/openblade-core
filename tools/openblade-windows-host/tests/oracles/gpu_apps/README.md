# Managed Session GPU parity oracle

Offline console that runs the production managed Session GPU codec
(`SessionGpuIpcCodec`) and dispatcher (`SessionGpuRequestDispatcher`, compiled
from its source file) against scripted scans and action results. It references
`OpenBlade.Session` and `OpenBlade.Platform` only for their types; no GPU
counter, process, window, endpoint, installed product, SCM or UAC is involved.

Modes:

- `requests-emit <out.jsonl>`: writes `{case, valid, frame}` rows (frame is
  base64 of the length-prefixed bytes). Valid rows are encoded by
  `SessionGpuIpcCodec.WriteRequestAsync`; invalid rows are hand-built JSON that
  `ReadRequestAsync` must reject (the run fails otherwise). Output is committed
  as `tests/fixtures/gpu-requests/managed-requests.jsonl`. The managed reader
  also accepts encodings it never writes (duplicate members, other member-name
  casing, numbers given as strings); those are printed as observations and are
  not fixture rows, because native refuses them. Upper-case GUIDs and extra
  whitespace are printed too; both readers accept them.
- `requests-verify <in.jsonl>`: decodes every valid row with `ReadRequestAsync`,
  requires the managed re-encode to equal the row bytes, and requires every
  invalid row to be rejected.
- `dispatch-emit <out.jsonl>`: runs the managed dispatcher over scripted scans
  and action results (including an operation stopped part way through) and
  writes `{case, request, scan, response}` rows. Output is committed as
  `tests/fixtures/gpu-requests/managed-dispatch.jsonl`. The native test feeds
  the same scan and results through its own response mapping and requires the
  same response.
- `responses-verify <native.jsonl>`: decodes `{case, frame}` rows written by the
  native tests (`OPENBLADE_NATIVE_AGENT_GPU_FRAMES`) with `ReadResponseAsync`,
  including its validation, and requires every member to bind.

Run from the repository root:

```powershell
$run = 'C:\tmp\gpu-apps-parity'
$project = 'tools/openblade-windows-host/tests/oracles/gpu_apps/GpuAppsParity.csproj'
dotnet restore $project -p:ArtifactsPath="$run\artifacts" -p:NuGetAudit=false
dotnet build $project --no-restore -p:ArtifactsPath="$run\artifacts"
$oracle = "$run\artifacts\bin\GpuAppsParity\debug\GpuAppsParity.dll"
$fx = 'tools/openblade-windows-host/tests/fixtures/gpu-requests'
dotnet $oracle requests-emit "$fx/managed-requests.jsonl"
dotnet $oracle requests-verify "$fx/managed-requests.jsonl"
dotnet $oracle dispatch-emit "$fx/managed-dispatch.jsonl"

$env:OPENBLADE_NATIVE_AGENT_GPU_FRAMES = "$run\native-frames.jsonl"
Remove-Item $env:OPENBLADE_NATIVE_AGENT_GPU_FRAMES -ErrorAction Ignore  # rows are appended
cargo test --locked --manifest-path tools/openblade-windows-host/Cargo.toml `
    --bin openblade-windows-agent-candidate --features agent-gpu-v1 -- gpu::request_parity
dotnet $oracle responses-verify $env:OPENBLADE_NATIVE_AGENT_GPU_FRAMES
```

Passing proves bounded wire and response-mapping parity only. It does not prove
GPU counter sampling, process identity, window or termination behavior, the
installed runtime, or hardware behavior.
