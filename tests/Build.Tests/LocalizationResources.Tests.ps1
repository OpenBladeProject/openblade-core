[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$validator = Join-Path $repository 'build\Validate-LocalizationResources.ps1'

$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $validator,
    [ref]$null,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Localization validator contains parser errors: $($parseErrors -join '; ')"
}

& $validator -Repository $repository

$utf8 = [Text.UTF8Encoding]::new($false, $true)
$trayEnglish = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'), $utf8)
$trayPseudo = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'src\OpenBlade.Tray\Strings\qps-ploc\Resources.resw'), $utf8)
$installerEnglish = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx'), $utf8)
$installerPseudo = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx'), $utf8)

if (@($trayEnglish.root.data).Count -lt 400) {
    throw 'The tray en-US catalog unexpectedly lost critical-page coverage.'
}
if (@($trayEnglish.root.data).Count -ne @($trayPseudo.root.data).Count) {
    throw 'The tray pseudo catalog must match en-US key count.'
}
if (@($installerEnglish.root.data).Count -lt 45) {
    throw 'The installer en-US catalog unexpectedly lost interactive-state coverage.'
}
if (@($installerEnglish.root.data).Count -ne @($installerPseudo.root.data).Count) {
    throw 'The installer pseudo catalog must match en-US key count.'
}

Write-Host 'Localization resource tests passed.'
