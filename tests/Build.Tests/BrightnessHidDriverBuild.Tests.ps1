[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repository 'build\Build-BrightnessHidDriver.ps1'
$source = Get-Content -Raw -LiteralPath $scriptPath
$publishSource = Get-Content -Raw -LiteralPath (
    Join-Path $repository 'build\Publish.ps1')

function Assert-Ordered {
    param(
        [Parameter(Mandatory)][string]$Earlier,
        [Parameter(Mandatory)][string]$Later
    )

    $earlierIndex = $source.IndexOf($Earlier, [StringComparison]::Ordinal)
    $laterIndex = $source.IndexOf($Later, [StringComparison]::Ordinal)
    if ($earlierIndex -lt 0 -or $laterIndex -lt 0 -or $earlierIndex -ge $laterIndex) {
        throw "Driver build ordering contract is invalid: '$Earlier' must precede '$Later'."
    }
}

if ($publishSource.IndexOf(
        '-not $_.FullName.StartsWith(',
        [StringComparison]::Ordinal) -lt 0 -or
    $publishSource.IndexOf(
        '$brightnessHidPayload + [IO.Path]::DirectorySeparatorChar',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Generic application signing must exclude the cataloged brightness HID payload.'
}

Assert-Ordered "@('OpenBlade.BrightnessHid.dll')" '& $inf2Cat.FullName'
Assert-Ordered '& $inf2Cat.FullName' 'Driver catalog signing failed'
Assert-Ordered 'does not verify exact member' '$inf = Get-Content'

foreach ($contract in @(
        'Microsoft Windows Hardware Compatibility Publisher',
        'verify /kp /c',
        'does not verify exact member',
        'does not match product version',
        'ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID',
        'OpenBlade.BrightnessHid.cat',
        'OpenBlade.BrightnessHid.dll')) {
    if ($source.IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "Driver build script is missing required contract '$contract'."
    }
}

if ($source.IndexOf(
        '.SignerCertificate.Subject.Contains(',
        [StringComparison]::Ordinal) -ge 0 -or
    $source.IndexOf(
        '.SignerCertificate.Subject.IndexOf(',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'The Hardware Dev Center signer check must remain compatible with Windows PowerShell 5.1.'
}

$driverInvocationEnd = $publishSource.IndexOf(
    '-TimestampUrl $TimestampUrl',
    [StringComparison]::Ordinal)
if ($driverInvocationEnd -lt 0) {
    throw 'Publish.ps1 no longer invokes the brightness HID driver build.'
}
$trayAssetsStart = $publishSource.IndexOf(
    '$trayAssets = Join-Path',
    $driverInvocationEnd,
    [StringComparison]::Ordinal)
if ($trayAssetsStart -lt 0 -or
    $publishSource.Substring(
        $driverInvocationEnd,
        $trayAssetsStart - $driverInvocationEnd).IndexOf(
            '$LASTEXITCODE',
            [StringComparison]::Ordinal) -ge 0) {
    throw 'Publish.ps1 must rely on terminating driver-build errors instead of stale LASTEXITCODE state.'
}

Write-Host 'Brightness HID driver build contract tests passed.'
