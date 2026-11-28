# Managed Display parity oracle

Offline console that runs the production managed Display code against native
Rust output. It references `OpenBlade.Session`; no OS supplier, installed
product, endpoint, hardware, SCM or UAC is involved.

Modes:

- `DisplayQueryParity <frames.jsonl>`: each row holds one native response frame.
  Rows with 4 members (case, expectedStatus, expectedState, frame) are read-only
  queries and must be Succeeded. Rows with 5 members add expectedError and cover
  setBrightness, setHdr, setRefreshRate, setColorProfile and applyProfile results
  (0 Succeeded, 1 Blocked, 2 Failed, 3 Indeterminate). Each frame must decode
  with `SessionDisplayIpcCodec`, match status, error and state exactly, and
  survive a managed roundtrip. The expected texts come from the Rust test
  tables, checked by hand against the managed controller source: this mode
  proves the codec round-trip of those frames, not that managed would produce
  the same text for the same failure.
- `checkpoints-emit <out.jsonl>`: writes exact checkpoint bytes using the
  production `DisplayAutomationCheckpointStore` (schema 4 and one legacy schema 1)
  with the expected state. Output is committed as
  `tests/fixtures/display-automation/managed-checkpoints.jsonl`.
- `checkpoints-verify <native.jsonl>`: loads bytes written by the native Rust
  store through `LoadRecovery`, requires every member to equal the expected
  state, and requires a save/load roundtrip. Rows marked `expectedInvalid` must
  load as null.
- `requests-emit <out.jsonl>`: writes `{case, valid, frame}` rows (frame is base64
  of the length-prefixed bytes). Valid rows are encoded by
  `SessionDisplayIpcCodec.WriteRequestAsync`; invalid rows are hand-built JSON
  that `ReadRequestAsync` must reject (the run fails otherwise). Output is
  committed as `tests/fixtures/display-requests/managed-requests.jsonl`.
  The managed reader accepts duplicate members (last wins) and applyProfile
  settings it never writes (a missing `brightnessPercent` or `isConfigured`,
  an inconsistent `isConfigured`, PascalCase names, a string percent), so those
  cases are printed as observations and are not fixture rows. It rejects `operation`
  when it is not the first member, a request given as a JSON array, and applyProfile
  settings given as a JSON array (fixture rows; a serde-derived struct alone would
  bind the members by position).
- `requests-verify <in.jsonl>`: decodes every valid row with `ReadRequestAsync`,
  requires the managed re-encode to equal the row bytes, and requires every
  invalid row to be rejected.

Run from the repository root (long worktree paths may need `subst X: <worktree>`):

```powershell
$run = 'C:\tmp\display-query-parity'
$project = 'tools/openblade-windows-host/tests/oracles/display_query/DisplayQueryParity.csproj'
dotnet restore $project -p:ArtifactsPath="$run\artifacts" -p:NuGetAudit=false
dotnet build $project --no-restore -p:ArtifactsPath="$run\artifacts"
dotnet "$run\artifacts\bin\DisplayQueryParity\debug\DisplayQueryParity.dll" checkpoints-emit '<out.jsonl>'
```

Passing proves bounded wire and storage parity only. It does not prove Windows
supplier behavior, the installed runtime or hardware behavior.

Regenerate and re-check the request fixture with the same build:

```powershell
$fx = 'tools/openblade-windows-host/tests/fixtures/display-requests/managed-requests.jsonl'
dotnet "$run\artifacts\bin\DisplayQueryParity\debug\DisplayQueryParity.dll" requests-emit $fx
dotnet "$run\artifacts\bin\DisplayQueryParity\debug\DisplayQueryParity.dll" requests-verify $fx
```
