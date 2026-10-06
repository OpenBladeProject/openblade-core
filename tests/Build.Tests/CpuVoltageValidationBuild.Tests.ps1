$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build/BuildIdentity.ps1')
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repository 'build/Publish.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Publish.ps1 must parse.' }
$statements = $ast.EndBlock.Statements
$profileBranches = @($statements | Where-Object { $_.Extent.Text.StartsWith('if ($CpuVoltageValidation)') })
if ($profileBranches.Count -ne 2) { throw 'Expected profile admission and shared publish-argument branches.' }
$initialArguments = $statements | Where-Object { $_.Extent.Text.StartsWith('$publishArguments = @(') }
$versionArguments = $statements | Where-Object { $_.Extent.Text.StartsWith("if (`$buildIdentity.Channel -ne 'Stable')") }
$gate = [scriptblock]::Create($profileBranches[0].Extent.Text)
$arguments = [scriptblock]::Create($initialArguments.Extent.Text + "`n" +
    $profileBranches[1].Extent.Text + "`n" + $versionArguments.Extent.Text)

# Execute the actual publisher gate/argument construction, replacing only Git.
# No publish command, filesystem mutation, native or installed operation is run.
$SourceRevision = '1234567890123456789012345678901234567890'
$script:validationTestHead = $SourceRevision
$script:validationTestDirty = ''
function git {
    $global:LASTEXITCODE = 0
    if ($args -contains 'rev-parse') { return $script:validationTestHead }
    if ($args -contains 'status') {
        if ($args -contains '--untracked-files=no' -and $script:validationTestDirty.StartsWith('?? ')) { return '' }
        return $script:validationTestDirty
    }
    throw 'Unexpected Git command.'
}
$Configuration = 'Release'
$ThermalExternalDriftValidation = $false
$checks = 0
foreach ($channel in @('Stable', 'Nightly', 'LocalDev')) {
    foreach ($enabled in @($false, $true)) {
        $CpuVoltageValidation = $enabled
        $buildIdentity = Resolve-OpenBladeBuildIdentity -BaseVersion '0.24.0' -Channel $channel `
            -BuildRevision $(if ($channel -eq 'Nightly') { 1 } else { 0 }) `
            -LocalDevVersion $(if ($channel -eq 'LocalDev') { '65535.1.2.3' } else { $null }) -SourceRevision $SourceRevision
        $rejected = $false
        try { . $gate } catch { $rejected = $true }
        if ($enabled -and $channel -ne 'LocalDev') {
            if (-not $rejected) { throw "$channel admitted the validation profile." }
        }
        else {
            if ($rejected) { throw "$channel unexpectedly rejected profile=$enabled." }
            . $arguments
            $expected = '--property:OpenBladeCpuVoltageValidation=' + $enabled.ToString().ToLowerInvariant()
            if ($publishArguments -notcontains $expected) { throw 'Missing explicit shared profile selection.' }
            if ($enabled -and $publishArguments -notcontains (
                    '--property:InformationalVersion=65535.1.2.3+voltage-validation.' + $SourceRevision.Substring(0, 8))) {
                throw 'LocalDev lost its distinct exact-source validation version.'
            }
        }
        $checks++
    }
}
$CpuVoltageValidation = $true
$buildIdentity = Resolve-OpenBladeBuildIdentity -BaseVersion '0.24.0' -Channel LocalDev `
    -LocalDevVersion '65535.65534.65534.65534' -SourceRevision $SourceRevision
. $gate
. $arguments
if ($buildIdentity.InformationalVersion -ne ('65535.65534.65534.65534+voltage-validation.' + $SourceRevision.Substring(0, 8)) -or
    $buildIdentity.InformationalVersion.Length -gt 64 -or $buildIdentity.SourceRevision -ne $SourceRevision -or
    $publishArguments -notcontains "--property:SourceRevision=$SourceRevision") {
    throw 'Maximum LocalDev profile must fit IPC and retain the full source revision.'
}
$checks++
foreach ($failure in @('revision', 'dirty', 'untracked', 'combined', 'malformed')) {
    $buildIdentity = Resolve-OpenBladeBuildIdentity -BaseVersion '0.24.0' -Channel LocalDev `
        -LocalDevVersion '65535.1.2.3' -SourceRevision $SourceRevision
    $script:validationTestHead = if ($failure -eq 'revision') { 'a' * 40 } else { $SourceRevision }
    $script:validationTestDirty = if ($failure -eq 'dirty') { ' M file.cs' } elseif ($failure -eq 'untracked') { '?? src/OpenBlade.Service/Untracked.cs' } else { '' }
    $ThermalExternalDriftValidation = $failure -eq 'combined'
    $savedRevision = $SourceRevision
    if ($failure -eq 'malformed') { $SourceRevision = 'arbitrary' }
    $rejected = $false
    try { . $gate } catch { $rejected = $true }
    $SourceRevision = $savedRevision
    if (-not $rejected) { throw "Publisher accepted $failure source/profile." }
    $checks++
}
Write-Host "CPU voltage publisher checks passed: $checks"
