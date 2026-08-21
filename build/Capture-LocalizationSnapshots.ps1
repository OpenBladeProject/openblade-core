[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts\localization\issue-233',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('qps-ploc', 'zh-CN', 'ru-RU')]
    [string]$Culture = 'qps-ploc'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$resolvedOutput = [IO.Path]::GetFullPath((
    Join-Path (Join-Path $repository $OutputDirectory) $Culture))
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
try {
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
}

$appliedDpi = try {
    (Get-ItemProperty -LiteralPath 'HKCU:\Control Panel\Desktop\WindowMetrics' `
        -Name AppliedDPI -ErrorAction Stop).AppliedDPI
} catch { $null }
$lightTheme = try {
    (Get-ItemProperty -LiteralPath (
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize') `
        -Name AppsUseLightTheme -ErrorAction Stop).AppsUseLightTheme
} catch { $null }
$images = @(Get-ChildItem -LiteralPath $resolvedOutput -Filter '*.png' -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = Get-SnapshotRelativePath $resolvedOutput $_.FullName
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = $_.Length
        }
    })
$manifest = [ordered]@{
    sourceRevision = (git -C $repository rev-parse HEAD).Trim()
    workingTreeDiff = (git -C $repository diff --stat)
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    culture = $Culture
    operatingSystem = [Environment]::OSVersion.VersionString
    appliedDpi = $appliedDpi
    appTheme = if ($null -eq $lightTheme) { 'unknown' }
        elseif ($lightTheme -eq 0) { 'dark' } else { 'light' }
    images = $images
}
[IO.File]::WriteAllText(
    (Join-Path $resolvedOutput 'manifest.json'),
    ($manifest | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

Write-Host "Localization snapshots written to $resolvedOutput"
