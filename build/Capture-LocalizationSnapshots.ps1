[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts\localization\snapshots',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('en-US', 'qps-ploc', 'zh-CN', 'ru-RU')]
    [string]$Culture = 'qps-ploc'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$resolvedOutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
}
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $resolvedOutputRoot $Culture))
$trayOutput = Join-Path $resolvedOutput 'tray'
$installerOutput = Join-Path $resolvedOutput 'installer'
$failureEvidenceRoot = Join-Path $repository (Join-Path (
    "artifacts\translator-archive-failures\snapshot-stability\$Culture") (
    [Guid]::NewGuid().ToString('N')))
[IO.Directory]::CreateDirectory($trayOutput) | Out-Null
[IO.Directory]::CreateDirectory($installerOutput) | Out-Null

function Get-SnapshotRelativePath {
    param([string]$Root, [string]$Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Snapshot is outside the output directory: $fullPath"
    }
    return $fullPath.Substring($prefix.Length)
}

function Write-SnapshotFailureEvidenceStatus {
    param([Parameter(Mandatory)][string]$Root)

    try {
        if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
            Write-Warning "Snapshot failure evidence root does not exist: $Root" `
                -WarningAction Continue
            return
        }
        $metadata = @(Get-ChildItem -LiteralPath $Root -Filter 'metadata.json' `
                -File -Recurse -ErrorAction Stop)
        if ($metadata.Count -eq 0) {
            Write-Warning (
                "Snapshot failure evidence root exists but is partial " +
                "(metadata.json completion marker absent): $Root") `
                -WarningAction Continue
            return
        }
        Write-Warning "Completed snapshot failure evidence found at: $Root" `
            -WarningAction Continue
    }
    catch {
        Write-Warning "Snapshot failure evidence status is unavailable: $Root" `
            -WarningAction Continue
    }
}

dotnet build (Join-Path $repository 'src\OpenBlade.Tray\OpenBlade.Tray.csproj') `
    -c $Configuration -p:NuGetAudit=false -p:OpenBladeLocalizationPreview=true --no-restore
if ($LASTEXITCODE -ne 0) { throw 'The tray snapshot build failed.' }

if (-not ('OpenBlade.Testing.WindowsVisualTestDesktop' -as [type])) {
    Add-Type -Path (Join-Path $repository 'tests\OpenBlade.Testing\WindowsVisualTestDesktop.cs')
}
$desktop = New-Object OpenBlade.Testing.WindowsVisualTestDesktop

$previousSnapshotDirectory = $env:OPENBLADE_LOCALIZATION_SNAPSHOT_DIR
$previousPseudo = $env:OPENBLADE_PSEUDO_LOCALIZATION
$previousLanguage = $env:OPENBLADE_LOCALIZATION_LANGUAGE
$previousSnapshotState = $env:OPENBLADE_LOCALIZATION_SNAPSHOT_STATE
$previousSnapshotPage = $env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE
$previousFailureEvidenceDirectory = $env:OPENBLADE_LOCALIZATION_FAILURE_EVIDENCE_DIR
$previousCompatibilityLayer = $env:__COMPAT_LAYER
try {
    # Constrain every child renderer to a 96-DPI logical desktop without
    # changing the user's display configuration or machine-wide settings.
    # Failure evidence stays outside the disposable snapshot/package trees so
    # this capture and translator export workflow can retain timed-out frames.
    $env:__COMPAT_LAYER = 'DPIUNAWARE'
    $env:OPENBLADE_PSEUDO_LOCALIZATION = if ($Culture -eq 'qps-ploc') { '1' } else { $null }
    $env:OPENBLADE_LOCALIZATION_LANGUAGE = $Culture
    $env:OPENBLADE_LOCALIZATION_FAILURE_EVIDENCE_DIR = $failureEvidenceRoot
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_DIR = $installerOutput
    $installerStates = @(
        'install',
        'maintenance',
        'newer-version',
        'progress',
        'cancel',
        'success',
        'failure')
    foreach ($installerState in $installerStates) {
        $env:OPENBLADE_LOCALIZATION_SNAPSHOT_STATE = $installerState
        dotnet test (Join-Path $repository (
            'build\installer\OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj')) `
            -c $Configuration -p:NuGetAudit=false --no-restore `
            --filter 'FullyQualifiedName~LocalizationSnapshotsRenderEveryCriticalInstallerState'
        if ($LASTEXITCODE -ne 0) {
            throw "The installer '$installerState' snapshot render failed."
        }
    }

    $trayExecutable = Join-Path $repository (
        "src\OpenBlade.Tray\bin-localization-preview\$Configuration\net10.0-windows10.0.26100.0\win-x64\OpenBlade.LocalizationPreview.exe")
    if (-not (Test-Path -LiteralPath $trayExecutable -PathType Leaf)) {
        throw "Tray executable not found: $trayExecutable"
    }
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_DIR = $trayOutput
    foreach ($trayPage in @(
            'home',
            'profiles',
            'profiles-narrow',
            'profiles-wide',
            'profiles-applications',
            'profiles-applications-empty',
            'profiles-application-editor',
            'profiles-application-editor-controls',
            'profiles-application-discovery',
            'profiles-application-discovery-empty',
            'telemetry',
            'telemetry-empty',
            'telemetry-disabled',
            'telemetry-analysis',
            'telemetry-narrow',
            'telemetry-controls-narrow',
            'telemetry-hover-edge',
            'telemetry-dark',
            'telemetry-range',
            'telemetry-expired',
            'telemetry-retention',
            'telemetry-recording',
            'telemetry-replay',
            'telemetry-metrics',
            'telemetry-color',
            'laptop',
            'laptop-battery-history',
            'laptop-snap-tap-info',
            'thx-audio',
            'thx-audio-headphones',
            'thx-audio-enhancements',
            'thx-audio-applications',
            'lighting',
            'display',
            'display-brightness-known',
            'display-brightness-unavailable',
            'settings',
            'settings-appearance',
            'about',
            'tray-dashboard')) {
        $env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE = $trayPage
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $trayExecutable
        $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($trayExecutable)
        $startInfo.UseShellExecute = $false
        $process = $desktop.Start($startInfo)
        try {
            if (-not $process.WaitForExit(60000)) {
                $process.Kill()
                if (-not $process.WaitForExit(5000)) {
                    throw "The tray '$trayPage' snapshot process did not exit after termination."
                }
                throw "The tray '$trayPage' snapshot render timed out."
            }
            if ($process.ExitCode -ne 0) {
                $report = Join-Path $trayOutput 'snapshot-report.txt'
                $diagnostic = if (Test-Path -LiteralPath $report) {
                    [IO.File]::ReadAllText($report)
                } else { 'No snapshot report was written.' }
                throw "The tray '$trayPage' snapshot render failed with $($process.ExitCode): $diagnostic"
            }
        }
        finally {
            $process.Dispose()
        }
    }
}
catch {
    try {
        Write-SnapshotFailureEvidenceStatus -Root $failureEvidenceRoot
    }
    catch {
        # Diagnostics must never replace the renderer failure being rethrown.
    }
    throw
}
finally {
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_DIR = $previousSnapshotDirectory
    $env:OPENBLADE_PSEUDO_LOCALIZATION = $previousPseudo
    $env:OPENBLADE_LOCALIZATION_LANGUAGE = $previousLanguage
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_STATE = $previousSnapshotState
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE = $previousSnapshotPage
    $env:OPENBLADE_LOCALIZATION_FAILURE_EVIDENCE_DIR = $previousFailureEvidenceDirectory
    $env:__COMPAT_LAYER = $previousCompatibilityLayer
    $desktop.Dispose()
}

$images = @(Get-ChildItem -LiteralPath $resolvedOutput -Filter '*.png' -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = (Get-SnapshotRelativePath $resolvedOutput $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = $_.Length
        }
    })
$manifest = [ordered]@{
    schemaVersion = 2
    culture = $Culture
    renderProfile = [ordered]@{
        language = $Culture
        theme = 'light'
        timeZone = 'UTC-for-snapshot-timestamps'
        dpi = '96-process-local-DPIUNAWARE'
        installerRendering = 'software'
        pixelCanonicalization = 'bgra8-4-bit-midpoint-local-majority-edge-alpha-filled-corner-v4'
        frameStabilization = 'two-consecutive-canonical-frames-v1'
        windowActivation = 'stable-title-foreground-v1'
        inputIsolation = 'noninteractive-window-v1'
        data = 'sanitized-fixed-preview'
    }
    images = $images
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5
$manifestJson = $manifestJson.Replace("`r`n", "`n").Replace("`r", "`n") + "`n"
[IO.File]::WriteAllText(
    (Join-Path $resolvedOutput 'manifest.json'),
    $manifestJson,
    [Text.UTF8Encoding]::new($false))

Write-Host "Localization snapshots written to $resolvedOutput"
