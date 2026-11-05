# Mailbox observation and inspection tools

Developer tooling for the [SO6120 caller-challenge plan](../../docs/so6120-mailbox-trigger-test-plan.md).
The first [live calibration](../../docs/so6120-mailbox-calibration.md) stopped
cleanly but exposed a failed transient-name control and insufficient budget
for the current action windows. Resolve those before a physical-action batch.
The recorder collects Windows ETW metadata only. Physical
actions stay with the operator. There is no port I/O, driver loading, AML
evaluation, fan/profile setter or self-elevation in these scripts.

Requirements: Python 3.11+ for inspection; x64 PowerShell 7.4+ on Windows for
recording and offline ETL export. The Python inspections also run on Linux.
The separate [firmware CLI](../firmware/README.md) remains offline-only.

## Preview a batch

From the repository root, this prints a plan and creates no trace:

```powershell
pwsh -NoProfile -File tools/mailbox-observation/Start-MailboxObservation.ps1
```

When entering the command at a PowerShell prompt, use the script directly so
PowerShell binds the action array:

```powershell
& tools/mailbox-observation/Start-MailboxObservation.ps1 -Actions Calibration,TypeC,AcPower,ThermalProfile
```

## Record an agreed batch

This is a separate, attended execution step. Use a fresh administrator
PowerShell console after the specific batch has been approved. The previous
one-run diagnostic authorizations do not cover a new recording.

```powershell
& tools/mailbox-observation/Start-MailboxObservation.ps1 `
  -Actions Calibration,TypeC,AcPower,ThermalProfile `
  -WindowSeconds 60 -Record `
  -Consent 'Record SO6120 mailbox observation only'
```

Choose only applicable actions. `Calibration` is required first and can be
run alone. It records for five seconds. A batch with physical actions adds
one immediate Handle-only baseline before the first action. Each action
window is 50–60 seconds, default 60, with an overall 60-second setup/recording
ceiling per window. Prompts require enough remaining time for both settling
periods and operator response. Enter arms an action while tracing is stopped;
wait for its departure/return cues and press D/R **after** completing each
edge. Q or Escape cancels. Ctrl+C requests cleanup. Do not terminate the
process to stop recording.

If recording ends early after an action, restore the agreed physical baseline
or confirmed profile using the supported control. The recorder only reminds
you; it cannot restore hardware. Ambiguous hardware submissions retain the
existing stop-and-reconcile rule. D/R marks are operator reports, not independent
confirmation of a power, peripheral or profile change.

Each run creates `artifacts/mailbox-observations/<run-id>/manifest.json`, a held
marker file, per-window transient marker files, and uniquely named ETLs. Existing
artifacts are never overwritten. The manifest preserves native stage codes,
loss, file hashes, planned and actual interval boundaries, operator marks and
cleanup. A failed publication attempts a separate `manifest.failed.json`.

Object recording uses the Handle keyword only, a 128 MiB cap, and a conservative
64 MiB disk watermark reduced for larger actual buffer allocation. ACPI has
a 16 MiB cap. Calibration gates numeric collection integrity and estimated
volume; **marker validity is checked offline afterward**. A failed marker
invalidates the associated measurement even when recording says `captured`.
Clean collection never means that ACPI target coverage has been established.

## Export and inspect saved traces

These steps read existing local files. They start no trace and require no
administrator elevation by design. The ETL exporter uses Windows' event-file
reader, preserving undecoded raw payloads and flagging unsupported forms.
It publishes only complete JSONL exports and has input, output, event-count
and elapsed-time bounds.

```powershell
$run = 'artifacts/mailbox-observations/REPLACE_WITH_RUN_ID'
$manifest = Get-Content -Raw -LiteralPath "$run/manifest.json" | ConvertFrom-Json
foreach ($window in $manifest.windows) {
  foreach ($session in $window.sessions) {
    # Run only for existing ETLs from this manifest. An incomplete session stays inconclusive.
    $etl = Join-Path $run $session.file
    if (Test-Path -LiteralPath $etl -PathType Leaf) {
      & tools/mailbox-observation/Export-MailboxTrace.ps1 `
        -EtlPath $etl -OutputPath "$etl.jsonl"
    }
  }
}
python -B tools/mailbox-observation/analyze_observation.py `
  --manifest "$run/manifest.json" --output "$run/inspection.json"
```

The analyzer binds each export to the manifest's ETL size/hash, validates raw
event layouts and File type, checks the held and transient controls, and
recomputes action timing and native-stage integrity. Relative marker names
are matched through the unique recorded run ID, so copied run folders remain
inspectable. Keep the marker files with the ETLs and manifest when copying.
Its report counts unnamed/unpaired handles and unknown layouts; it never
produces automatic write admission or a collision probability.

`target_observed` means a named candidate handle or mailbox-related method
was recorded. It does not prove port use or a collision. `no_target_observed`
must be read with collection integrity and coverage limits. `controls_valid`
means those mechanical checks passed; ACPI emitter coverage, independent action
confirmation and acceptance of residual risk remain explicit review items.
For older captures without the new manifest, `--export FILE.jsonl --output NEW.json`
provides counts only and leaves control coverage unestablished.

EventLogReader omits the ETL-header pseudo-event, so its total is one lower
than xperf/tracerpt on the two saved verification inputs. All target-provider
counts matched: 238,555 handle rundown rows, 21,067 File handles (947 unnamed)
and 112 ACPI method events. The original held-file control also matched.
Raw paths and process identities in these local artifacts must not enter Git.

## Prepare the ACPI emitter inspection

```powershell
python -B tools/mailbox-observation/inspect_acpi_image.py `
  --input C:/Windows/System32/drivers/ACPI.sys `
  --metadata tools/mailbox-observation/event7-metadata.json `
  --output artifacts/acpi-image-inspection.json
```

The inspector reads the image as data, requires the reviewed SHA-256 and
complete Event 7 descriptor, and writes a fresh report. It maps sections,
imports and x64 unwind intervals, and locates descriptor-byte candidates.
The pinned input has one candidate at RVA/file offset `0x75D78` and 2,043
function intervals. These are starting points for the emitter audit, **not
decoded call references or proof of external/nested tracing coverage**.
A different image is refused; there is no CLI hash-pin override or download.

## Offline checks

```powershell
python -B -m unittest discover -s tools/mailbox-observation/tests -p 'test_*.py' -v
pwsh -NoProfile -File tools/mailbox-observation/tests/Test-MailboxTraceController.ps1
pwsh -NoProfile -File tools/mailbox-observation/tests/Test-MailboxTraceExporter.ps1
```

The controller tests inject fake native/session/runtime backends. Exporter
tests parse synthetic event XML and refuse invalid local ETLs. They do not
start a real ETW session or call a device. No vendor binary or raw trace is
part of the test fixtures or release payload.
