# Managed Battery.v1 dispatcher parity

This console reads JSONL emitted by the native selected-dispatcher test
`selected_dispatcher_emits_managed_frames_from_acquired_xml_and_failures` through
`OPENBLADE_NATIVE_AGENT_BATTERY_FRAMES`. Use a new, absent corpus path: the native
test appends, and this oracle deliberately rejects duplicates and incomplete runs.
The corpus is input only; no canned response fallback exists.

The console references the production Session assembly and links the production
`WindowsBatteryReportParser.cs`. It strictly decodes every native frame through
`SessionBatteryIpcCodec.ReadResponseAsync`, independently checks all ten statuses,
request correlation, exact errors/nullness, prefix length and complete stream
consumption, compares success fields and every interval with the managed parser
of the same timeline XML, then exercises the managed response roundtrip.

Offline PowerShell example from the repository root (build outputs remain in one
tracked scratch folder; remove that folder after retaining needed evidence):

```powershell
$run = 'C:\tmp\native-agent-battery-report-parity'
New-Item -ItemType Directory $run -Force | Out-Null
Set-Content "$run\NuGet.Config" '<configuration><packageSources><clear /></packageSources></configuration>'
$project = 'tools/openblade-windows-host/tests/oracles/battery_report/BatteryReportParity.csproj'
dotnet restore $project --configfile "$run\NuGet.Config" -p:ArtifactsPath="$run\artifacts" -p:NuGetAudit=false
dotnet build $project --no-restore -p:ArtifactsPath="$run\artifacts"
# Produce a fresh corpus by running only the named Rust dispatcher test with
# OPENBLADE_NATIVE_AGENT_BATTERY_FRAMES set; it uses scripted acquisition and
# an in-memory transport, never powercfg or the installed Agent.
dotnet "$run\artifacts\bin\BatteryReportParity\debug\BatteryReportParity.dll" '<fresh-native-frames.jsonl>'
```

A missing, empty, incomplete, duplicated, malformed, or mismatching corpus fails
with a nonzero exit. A passing run proves wire/parser parity for this bounded
software corpus. It does not prove Windows acquisition, endpoint identity,
installed readiness, physical behavior, or an installed-product cutover.
