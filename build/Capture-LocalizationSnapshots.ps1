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

dotnet build (Join-Path $repository 'src\OpenBlade.Tray\OpenBlade.Tray.csproj') `
    -c $Configuration -p:NuGetAudit=false -p:OpenBladeLocalizationPreview=true --no-restore
if ($LASTEXITCODE -ne 0) { throw 'The tray snapshot build failed.' }

$previousSnapshotDirectory = $env:OPENBLADE_LOCALIZATION_SNAPSHOT_DIR
$previousPseudo = $env:OPENBLADE_PSEUDO_LOCALIZATION
$previousLanguage = $env:OPENBLADE_LOCALIZATION_LANGUAGE
$previousSnapshotState = $env:OPENBLADE_LOCALIZATION_SNAPSHOT_STATE
$previousSnapshotPage = $env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE
$previousCompatibilityLayer = $env:__COMPAT_LAYER
try {
    # Constrain every child renderer to a 96-DPI logical desktop without
    # changing the user's display configuration or machine-wide settings.
    $env:__COMPAT_LAYER = 'DPIUNAWARE'
    $env:OPENBLADE_PSEUDO_LOCALIZATION = if ($Culture -eq 'qps-ploc') { '1' } else { $null }
    $env:OPENBLADE_LOCALIZATION_LANGUAGE = $Culture
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
            'laptop',
            'lighting',
            'display',
            'settings',
            'settings-appearance',
            'about',
            'tray-dashboard')) {
        $env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE = $trayPage
        $process = Start-Process -FilePath $trayExecutable `
            -WorkingDirectory ([IO.Path]::GetDirectoryName($trayExecutable)) `
            -WindowStyle Hidden `
            -PassThru
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
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
}
finally {
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_DIR = $previousSnapshotDirectory
    $env:OPENBLADE_PSEUDO_LOCALIZATION = $previousPseudo
    $env:OPENBLADE_LOCALIZATION_LANGUAGE = $previousLanguage
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_STATE = $previousSnapshotState
    $env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE = $previousSnapshotPage
    $env:__COMPAT_LAYER = $previousCompatibilityLayer
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
        dpi = '96-process-local-DPIUNAWARE'
        installerRendering = 'software'
        pixelCanonicalization = 'bgra8-4-bit-midpoint-local-majority-v1'
        windowActivation = 'stable-title-foreground-v1'
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
