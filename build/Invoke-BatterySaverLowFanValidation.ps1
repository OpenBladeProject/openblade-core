[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CapturePath,
    [Parameter(Mandatory)][string]$RecordDirectory,
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$installDirectory = 'C:\Program Files\OpenBlade'
$captureFile = [IO.Path]::GetFullPath($CapturePath)
$recordRoot = [IO.Path]::GetFullPath($RecordDirectory)
$childReport = Join-Path $recordRoot 'owner-handoff.json'
$outputPath = Join-Path $recordRoot 'capture-result.json'
$journalPath = Join-Path $recordRoot 'hardware-journal.jsonl'
$manifestPath = Join-Path $recordRoot 'probe-hashes.json'

function Write-Report($Report) {
    [IO.File]::WriteAllText($childReport, ($Report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

if ($Elevated) {
    $report = [ordered]@{ CaptureStarted = $false; SafeToResume = $false; Restored = $false; Error = $null }
    $originalServiceProcess = $null
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($entry in $manifest) {
            if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) {
                throw 'A reviewed probe file changed before the owner handoff.'
            }
        }
        if ($captureFile -ne $manifest[0].Path) { throw 'The probe manifest does not identify this executable.' }
        $service = Get-Service -Name OpenBlade
        if ($service.Status -ne 'Running') { throw 'The installed service is no longer running.' }
        $installedService = Get-CimInstance Win32_Service -Filter "Name='OpenBlade'"
        $serviceExecutable = $installedService.PathName.Trim('"')
        if ($serviceExecutable -ne (Join-Path $installDirectory 'OpenBlade.Service.exe')) {
            throw 'The OpenBlade service executable is not the expected installed file.'
        }
        $originalServiceProcess = Get-Process -Id $installedService.ProcessId
        $null = $originalServiceProcess.Handle
        foreach ($name in @('OpenBlade.Tray', 'OpenBlade.SessionAgent')) {
            $owners = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
            foreach ($owner in $owners) {
                if ($owner.Path -ne (Join-Path $installDirectory "$name.exe")) {
                    throw "Unexpected executable path for $name."
                }
                Stop-Process -Id $owner.Id
                $owner.WaitForExit(5000) | Out-Null
                if (-not $owner.HasExited) { throw "The installed $name did not exit." }
            }
        }
        Stop-Service -Name OpenBlade
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(15))
        # SCM can publish Stopped before the process finishes releasing native resources.
        # Wait on this exact original process, never terminate or overlap service owners.
        if (-not $originalServiceProcess.WaitForExit(15000)) {
            throw 'The original installed service process has not finished exiting.'
        }
        $remainingOwners = @(Get-Process -Name 'OpenBlade.Service', 'OpenBlade.Tray', 'OpenBlade.SessionAgent' -ErrorAction SilentlyContinue)
        if ($remainingOwners.Count -ne 0) {
            $report.RemainingOwners = @($remainingOwners | Select-Object ProcessName, Id)
            throw 'An OpenBlade owner is still active.'
        }
        foreach ($entry in $manifest) {
            if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) {
                throw 'A reviewed probe file changed after the owner handoff.'
            }
        }
        $report.CaptureStarted = $true
        Write-Report $report
        & $captureFile validate-battery-saver-1500 --confirm-target RZ09-0581:1532:02E0:4.01 --journal $journalPath > $outputPath
        $report.CaptureExitCode = $LASTEXITCODE
        $result = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
        $report.Restored = $result.Restored -eq $true
        $report.SafeToResume = ($result.Restored -eq $true -or $result.WritesAttempted -eq 0) -and $result.ManualRecoveryRequired -eq $false
        if (-not $report.SafeToResume) {
            throw 'Restoration is not confirmed. Leave all OpenBlade owners stopped; reconcile read-only.'
        }
        Start-Service -Name OpenBlade
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15))
    }
    catch {
        $report.Error = $_.Exception.Message
        if (-not $report.CaptureStarted) {
            # No developer firmware operation ran; ordinary startup can recover the saved service state.
            try {
                $recoveryService = Get-Service -Name OpenBlade
                if ($recoveryService.Status -eq 'Stopped') {
                    if ($null -ne $originalServiceProcess -and -not $originalServiceProcess.HasExited) {
                        throw 'The original service process is still exiting; leave the service stopped until it has exited.'
                    }
                    Start-Service -Name OpenBlade
                }
                $recoveryService.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15))
                $report.SafeToResume = $true
            }
            catch {
                $report.Error += ' No probe ran; installed process recovery remains incomplete: ' + $_.Exception.Message
            }
        }
    }
    finally {
        if ($null -ne $originalServiceProcess) { $originalServiceProcess.Dispose() }
    }
    Write-Report $report
    if ($report.Error) { exit 2 }
    exit 0
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Start the validation from a normal-integrity shell so the installed user processes can be relaunched normally.'
}
if (Test-Path -LiteralPath $recordRoot) { throw 'Use a new record directory for this one-shot experiment.' }
if (-not (Test-Path -LiteralPath $captureFile -PathType Leaf)) { throw 'The built Capture executable is missing.' }
New-Item -ItemType Directory -Path $recordRoot | Out-Null
$protectedBefore = @('OpenBlade.Service.exe', 'OpenBlade.SessionAgent.exe') | ForEach-Object {
    Get-FileHash -LiteralPath (Join-Path $installDirectory $_) -Algorithm SHA256
}
$originalUserOwners = @{}
foreach ($name in @('OpenBlade.SessionAgent', 'OpenBlade.Tray')) {
    $owners = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if ($owners.Count -gt 1 -or ($owners.Count -eq 1 -and $owners[0].Path -ne (Join-Path $installDirectory "$name.exe"))) {
        throw "The original installed $name owner is ambiguous."
    }
    $originalUserOwners[$name] = $owners.Count -eq 1
}
$probeFiles = @($captureFile) + @(Get-ChildItem -LiteralPath (Split-Path -Parent $captureFile) -File |
    Where-Object { $_.Extension -in '.dll', '.json' } | Sort-Object Name | Select-Object -ExpandProperty FullName)
@($probeFiles | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 | Select-Object Path, Hash }) |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Add-Type -Path (Join-Path $installDirectory 'OpenBlade.Contracts.dll')
Add-Type -Path (Join-Path $installDirectory 'OpenBlade.Tray.Core.dll')
$client = [OpenBlade.Tray.ServiceClient]::new()
$timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(10))
try {
    $baseline = $client.ObserveStateAsync([OpenBlade.Contracts.LiveReadingSensors]::All, $timeout.Token).GetAwaiter().GetResult()
}
finally { $client.DisposeAsync().GetAwaiter().GetResult(); $timeout.Dispose() }
if (-not $baseline.DeviceVerified -or $baseline.WritesBlocked -or $baseline.ControlAccess.ToString() -ne 'Active' -or
    $baseline.ActivePowerSource.ToString() -ne 'Battery' -or
    $baseline.ConfirmedState.Performance.ToString() -ne 'BatterySaver' -or
    $baseline.ConfirmedState.Fans.ToString() -ne 'Automatic' -or
    $baseline.ConfirmedState.HyperBoostActive -ne $false) {
    throw 'The confirmed installed baseline must be Battery Saver / Automatic on battery with active write admission.'
}
function Get-Intent($Status) {
    [ordered]@{
        Profiles = $Status.Profiles
        DesiredState = $Status.DesiredState
        PerformanceOverride = $Status.PerformanceOverride
        FanOverride = $Status.FanOverride
        FanControlOverride = $Status.FanControlOverride
        ActiveApplicationProfileId = $Status.ActiveApplicationProfileId
    } | ConvertTo-Json -Depth 25 -Compress
}
$baselineIntent = Get-Intent $baseline
[IO.File]::WriteAllText((Join-Path $recordRoot 'installed-baseline.json'),
    [Text.Json.JsonSerializer]::Serialize($baseline, $baseline.GetType(), [Text.Json.JsonSerializerOptions]::new()),
    [Text.UTF8Encoding]::new($false))
$shellPath = Join-Path $PSHOME 'pwsh.exe'
$childArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'),
    '-CapturePath', ('"' + $captureFile + '"'), '-RecordDirectory', ('"' + $recordRoot + '"'), '-Elevated')
$child = Start-Process -FilePath $shellPath -ArgumentList $childArguments -Verb RunAs -WindowStyle Hidden -PassThru
$child.WaitForExit()
if (-not (Test-Path -LiteralPath $childReport)) { throw 'The elevated handoff did not produce a report; do not relaunch owners.' }
$handoff = Get-Content -LiteralPath $childReport -Raw | ConvertFrom-Json
if ($handoff.SafeToResume -ne $true) { throw "OpenBlade remains stopped: $($handoff.Error)" }
foreach ($entry in $protectedBefore) {
    if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) {
        throw 'A protected installed executable changed during validation.'
    }
}
foreach ($name in @('OpenBlade.SessionAgent', 'OpenBlade.Tray')) {
    if ($originalUserOwners[$name] -and @(Get-Process -Name $name -ErrorAction SilentlyContinue).Count -eq 0) {
        Start-Process -FilePath (Join-Path $installDirectory "$name.exe") -WorkingDirectory $installDirectory -WindowStyle Hidden
    }
}
$client = [OpenBlade.Tray.ServiceClient]::new()
$timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(20))
try {
    # Service and Session Agent startup may publish an unconfirmed first snapshot.
    # Reconcile read-only; no setting is replayed to repair a presentation delay.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            $after = $client.GetStatusAsync($timeout.Token).GetAwaiter().GetResult()
        }
        catch {
            if ($attempt -eq 4 -or $timeout.IsCancellationRequested) { throw }
            Start-Sleep -Seconds 1
            continue
        }
        if ($null -ne $after.ConfirmedState -and
            $after.ConfirmedState.Performance.ToString() -eq 'BatterySaver' -and
            $after.ConfirmedState.Fans.ToString() -eq 'Automatic' -and
            (Get-Intent $after) -eq $baselineIntent) { break }
        if ($attempt -lt 4) { Start-Sleep -Seconds 1 }
    }
}
finally { $client.DisposeAsync().GetAwaiter().GetResult(); $timeout.Dispose() }
[IO.File]::WriteAllText((Join-Path $recordRoot 'installed-restoration.json'),
    [Text.Json.JsonSerializer]::Serialize($after, $after.GetType(), [Text.Json.JsonSerializerOptions]::new()),
    [Text.UTF8Encoding]::new($false))
if ((Get-Intent $after) -ne $baselineIntent -or
    $after.ConfirmedState.Performance.ToString() -ne 'BatterySaver' -or
    $after.ConfirmedState.Fans.ToString() -ne 'Automatic') {
    throw 'The original installed state has not been confirmed; do not submit another setting.'
}
foreach ($name in @('OpenBlade.SessionAgent', 'OpenBlade.Tray')) {
    $owners = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    $expectedCount = if ($originalUserOwners[$name]) { 1 } else { 0 }
    if ($owners.Count -ne $expectedCount -or ($owners.Count -eq 1 -and $owners[0].Path -ne (Join-Path $installDirectory "$name.exe"))) {
        throw "The original installed $name process ownership was not confirmed."
    }
}
Get-Content -LiteralPath $outputPath
if ($handoff.Error) { throw $handoff.Error }
if ($handoff.CaptureExitCode -ne 0) { exit ([int]$handoff.CaptureExitCode) }
