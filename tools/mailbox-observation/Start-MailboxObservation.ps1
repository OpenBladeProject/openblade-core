[CmdletBinding()]
param(
    [ValidateSet('Calibration', 'TypeC', 'AcPower', 'ThermalProfile')]
    [string[]] $Actions = @('Calibration'),

    [ValidateRange(50, 60)]
    [int] $WindowSeconds = 60,

    [switch] $Record,

    [string] $Consent
)

$ErrorActionPreference = 'Stop'
$consentText = 'Record SO6120 mailbox observation only'
if ($Actions.Count -eq 0 -or $Actions[0] -ne 'Calibration' -or
    @($Actions | Group-Object).Count -ne $Actions.Count) {
    throw 'Choose distinct reviewed actions with Calibration first.'
}

if (-not $Record) {
    [ordered]@{
        mode = 'plan_only'
        actions = $Actions
        calibration_seconds = 5
        baseline = if ($Actions.Count -gt 1) { 'Immediate Handle-only rundown before the first action' }
        action_window_seconds = $WindowSeconds
        providers = @(
            @{ name = 'Kernel-Acpi'; keyword = 1; level = 4 }
            @{ name = 'System Object'; keyword = 2; level = 5 }
        )
        record_consent = $consentText
        observation_only = $true
    } | ConvertTo-Json -Depth 4
    return
}

if ($Consent -cne $consentText) {
    throw "Recording requires -Record -Consent '$consentText'."
}
if (-not $IsWindows -or $PSVersionTable.PSVersion -lt [version]'7.4' -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne
        [Runtime.InteropServices.Architecture]::X64) {
    throw 'Recording requires x64 PowerShell 7.4 or newer on Windows.'
}
if ([Console]::IsInputRedirected) {
    throw 'Recording requires an attended console.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Recording requires a PowerShell console already opened with administrator rights.'
    }
} finally {
    $identity.Dispose()
}

$controllerPath = Join-Path $PSScriptRoot 'MailboxTraceController.cs'
if (-not ('OpenBlade.MailboxObservation.Recorder' -as [type])) {
    Add-Type -Path $controllerPath
} else {
    throw 'Use a fresh PowerShell process so the recorder can load the reviewed source once.'
}
[OpenBlade.MailboxObservation.ActionKind[]] $typedActions = $Actions
[OpenBlade.MailboxObservation.Recorder]::Validate($typedActions, $WindowSeconds)

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$outputRoot = Join-Path $repoRoot 'artifacts/mailbox-observations'
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($outputRoot))
$reserve = ($Actions.Count * 144MB) + 64MB
if ($Actions.Count -gt 1) { $reserve += 128MB }
if ($drive.AvailableFreeSpace -lt $reserve) {
    throw 'Insufficient free space for the agreed trace caps and reserve.'
}
$runId = [guid]::NewGuid()
$directory = [OpenBlade.MailboxObservation.Recorder]::CreateRunDirectory($outputRoot, $runId)

function Write-NewJson([string] $Path, [string] $Json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Json)
    $target = [IO.Path]::GetFullPath($Path)
    $temporary = [IO.Path]::Combine([IO.Path]::GetDirectoryName($target),
        ('.' + [IO.Path]::GetFileName($target) + '.' + [guid]::NewGuid().ToString('N') + '.tmp'))
    $ownsTemporary = $false
    try {
        $file = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $ownsTemporary = $true
        try {
            $file.Write($bytes, 0, $bytes.Length)
            $file.Flush($true)
        } finally {
            $file.Dispose()
        }
        [IO.File]::Move($temporary, $target, $false)
        $ownsTemporary = $false
    } finally {
        if ($ownsTemporary) { [IO.File]::Delete($temporary) }
    }
}

$result = $null
$failure = $null
$runtime = $null
try {
    $backend = [OpenBlade.MailboxObservation.NativeTraceBackend]::new($directory)
    $runtime = [OpenBlade.MailboxObservation.ConsoleRuntime]::new()
    $result = [OpenBlade.MailboxObservation.Recorder]::Run($directory, $runId.ToString('N'),
        $typedActions, $WindowSeconds, $backend, $runtime)
} catch {
    $failure = $_
} finally {
    if ($null -ne $runtime) { $runtime.Dispose() }
}

$options = [Text.Json.JsonSerializerOptions]::new()
$options.PropertyNamingPolicy = [Text.Json.JsonNamingPolicy]::SnakeCaseLower
$options.WriteIndented = $true
$manifestPath = Join-Path $directory 'manifest.json'
$failedPath = Join-Path $directory 'manifest.failed.json'
$json = $null
try {
    if ($null -eq $result) { throw 'The recorder failed before returning its manifest.' }
    $json = [Text.Json.JsonSerializer]::Serialize([object]$result, $result.GetType(), $options)
    Write-NewJson $manifestPath $json
} catch {
    $writeFailure = $_
    if ($null -eq $json) {
        $json = [ordered]@{
            schema = 'openblade.mailbox-observation'
            schema_version = 1
            architecture = 'x64'
            run_id = $runId.ToString('N')
            status = 'incomplete'
            actions = $Actions
            windows = @()
            failure_reasons = @('manifest_publish_failed')
            acpi_coverage_status = 'unresolved'
        } | ConvertTo-Json -Depth 4
    }
    try { Write-NewJson $failedPath $json } catch {
        Write-Warning "Could not preserve the partial manifest at $failedPath."
    }
    if ($null -ne $failure) { Write-Warning $failure.Exception.Message }
    throw "Manifest publication failed at $manifestPath. Inspect the trace directory and any manifest.failed.json. $($writeFailure.Exception.Message)"
}

[pscustomobject]@{
    manifest = $manifestPath
    collection_status = $result.Status
    marker_validation = 'pending_offline'
    acpi_coverage_status = 'unresolved'
}
if ($result.Status -ne 'captured') {
    throw 'The recording is incomplete. Inspect the manifest before scheduling any other observation.'
}
