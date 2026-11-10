#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureDirectory)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$candidate = Join-Path $repository 'build/development-deployment/RustFixtureCandidate.ps1'
. (Join-Path $repository 'build/PublishPayloadValidation.ps1')

$service = 'openblade-windows-host.exe'
$agent = 'openblade-rust-session-agent.exe'
$manifestName = 'rust-fixture-candidate.json'
$signatureName = 'rust-fixture-candidate.sig'
$root = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.RustCandidate.' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $root 'fixture.zip'
$cases = [Collections.Generic.List[string]]::new()

function Assert-Rejected([string]$Case, [scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if (-not $_.Exception.Message.Contains($Expected)) { throw }
        $cases.Add($Case)
        return
    }
    throw "Expected refusal for $Case."
}

function Set-ZipEntry($Archive, [string]$Name, [byte[]]$Bytes) {
    $Archive.GetEntry($Name)?.Delete()
    $stream = $Archive.CreateEntry($Name).Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

function Assert-InvalidPackage([string]$Case, [scriptblock]$Edit, [string]$Expected, [string]$Key) {
    $copy = Join-Path $root "$Case.zip"
    [IO.File]::Copy($package, $copy)
    $archive = [IO.Compression.ZipFile]::Open($copy, 'Update')
    try { & $Edit $archive }
    finally { $archive.Dispose() }

    # Supply the rebuilt outer digest to exercise the inner verification layers.
    Assert-Rejected $Case {
        & $candidate -Package $copy -PackageSha256 (Get-FileHash -LiteralPath $copy).Hash -FixturePublicKey $Key
    } $Expected
}

New-Item -ItemType Directory -Path $root | Out-Null
try {
    $receipt = & $candidate -Package $package -FixtureDirectory $FixtureDirectory
    $verified = & $candidate @receipt
    if ($verified.installable -ne $false -or $verified.architecture -cne 'x64' -or
        $verified.sourceRevision -cne '41a3237e81188ed0780e89a2ca2d170da7d61db0' -or
        ($verified.roles.role -join ',') -cne 'Service,SessionAgent' -or
        ($verified.roles.file -join ',') -cne "$service,$agent") {
        throw 'Unexpected verified fixture identity.'
    }
    $cases.Add('create and verify ordinary pair')

    Assert-Rejected 'existing destination' {
        & $candidate -Package $package -FixtureDirectory $FixtureDirectory
    } 'already exists'
    & $candidate @receipt | Out-Null

    $images = @{}
    foreach ($name in @($service, $agent)) {
        $images[$name] = [IO.File]::ReadAllBytes((Join-Path $FixtureDirectory $name))
    }
    $mixedInput = New-Item -ItemType Directory -Path (Join-Path $root 'mixed-input')
    [IO.File]::WriteAllBytes((Join-Path $mixedInput.FullName $service), $images[$service])
    $absentOutput = Join-Path $root 'rejected.zip'
    Assert-Rejected 'missing source role' {
        & $candidate -Package $absentOutput -FixtureDirectory $mixedInput.FullName
    } $agent
    [IO.File]::WriteAllBytes((Join-Path $mixedInput.FullName $agent), $images[$service])
    Assert-Rejected 'mixed source roles' {
        & $candidate -Package $absentOutput -FixtureDirectory $mixedInput.FullName
    } 'Unexpected SHA-256 for SessionAgent'
    $oversizedSource = [IO.File]::OpenWrite((Join-Path $mixedInput.FullName $agent))
    try { $oversizedSource.SetLength(32MB + 1) }
    finally { $oversizedSource.Dispose() }
    Assert-Rejected 'oversized source role' {
        & $candidate -Package $absentOutput -FixtureDirectory $mixedInput.FullName
    } 'Expected a bounded fixture executable'
    if (Test-Path -LiteralPath $absentOutput) { throw 'Rejected sources created a package.' }

    $tampered = Join-Path $root 'appended.zip'
    [IO.File]::Copy($package, $tampered)
    [IO.File]::AppendAllText($tampered, 'extra bytes')
    Assert-Rejected 'appended bytes' {
        & $candidate -Package $tampered -PackageSha256 $receipt.PackageSha256 -FixturePublicKey $receipt.FixturePublicKey
    } 'differs from the detached receipt'

    Assert-InvalidPackage 'missing role' {
        param($zip) $zip.GetEntry($agent).Delete()
    } 'Missing candidate entry' $receipt.FixturePublicKey
    Assert-InvalidPackage 'changed role bytes' {
        param($zip)
        $changed = [byte[]]$images[$service].Clone()
        $changed[-1] = $changed[-1] -bxor 1
        Set-ZipEntry $zip $service $changed
    } 'Unexpected SHA-256 for Service' $receipt.FixturePublicKey
    Assert-InvalidPackage 'extra managed role' {
        param($zip) Set-ZipEntry $zip 'OpenBlade.SessionAgent.dll' ([byte[]]@(1))
    } 'Unexpected or duplicate candidate entry' $receipt.FixturePublicKey
    Assert-InvalidPackage 'duplicate role' {
        param($zip) $null = $zip.CreateEntry($service)
    } 'Unexpected or duplicate candidate entry' $receipt.FixturePublicKey
    Assert-InvalidPackage 'misnamed role' {
        param($zip)
        $zip.GetEntry($service).Delete()
        Set-ZipEntry $zip 'OpenBlade.Service.exe' $images[$service]
    } 'Unexpected or duplicate candidate entry' $receipt.FixturePublicKey
    Assert-InvalidPackage 'swapped roles with original valid signature' {
        param($zip)
        Set-ZipEntry $zip $service $images[$agent]
        Set-ZipEntry $zip $agent $images[$service]
    } 'Unexpected SHA-256 for Service' $receipt.FixturePublicKey
    Assert-InvalidPackage 'changed signature' {
        param($zip) Set-ZipEntry $zip $signatureName ([byte[]]::new(64))
    } 'signature did not verify' $receipt.FixturePublicKey

    # Mirror the signed scratch Setup tests' local P-256 trust and signature format.
    $testKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $otherPublicKey = [Convert]::ToBase64String($testKey.ExportSubjectPublicKeyInfo())
        Assert-Rejected 'wrong public key' {
            & $candidate -Package $package -PackageSha256 $receipt.PackageSha256 -FixturePublicKey $otherPublicKey
        } 'signature did not verify'
        Assert-InvalidPackage 'signed false architecture' {
            param($zip)
            $stream = [IO.StreamReader]::new($zip.GetEntry($manifestName).Open())
            try { $manifest = $stream.ReadToEnd().Replace('"architecture":"x64"', '"architecture":"arm64"') }
            finally { $stream.Dispose() }
            $bytes = [Text.Encoding]::UTF8.GetBytes($manifest)
            $signature = $testKey.SignData($bytes, 'SHA256', 'IeeeP1363FixedFieldConcatenation')
            Set-ZipEntry $zip $manifestName $bytes
            Set-ZipEntry $zip $signatureName $signature
        } 'differs from the reviewed fixture identity' $otherPublicKey
    }
    finally { $testKey.Dispose() }

    # Even mapping verified fixture bytes to the installed names does not satisfy
    # the ordinary publish gate. The managed role dependencies are still required.
    $production = New-Item -ItemType Directory -Path (Join-Path $root 'product')
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $production.FullName)
    Rename-Item -LiteralPath (Join-Path $production.FullName $service) -NewName 'OpenBlade.Service.exe'
    Rename-Item -LiteralPath (Join-Path $production.FullName $agent) -NewName 'OpenBlade.SessionAgent.exe'
    foreach ($name in @('OpenBlade.CpuTemperature.exe', 'OpenBlade.CpuTemperature.dll',
            'OpenBlade.CpuTemperature.deps.json', 'OpenBlade.CpuTemperature.runtimeconfig.json',
            'LibreHardwareMonitorLib.dll', 'PawnIO_setup.exe', 'PawnIO-NOTICE.txt',
            'OpenBlade.Platform.dll', 'OpenBlade.FanStop.Core.dll', 'OpenBlade.FanStop.Guardian.exe',
            'OpenBlade.FanStop.Guardian.dll', 'OpenBlade.FanStop.Guardian.deps.json',
            'OpenBlade.FanStop.Guardian.runtimeconfig.json')) {
        New-Item -ItemType File -Path (Join-Path $production.FullName $name) | Out-Null
    }
    New-Item -ItemType Directory -Path (Join-Path $production.FullName 'licenses') | Out-Null
    New-Item -ItemType File -Path (Join-Path $production.FullName 'licenses/LibreHardwareMonitor-source.zip') | Out-Null
    Assert-Rejected 'ordinary publish requires the managed Session Agent' {
        Assert-ServicePublishPayload -Payload $production.FullName
    } 'OpenBlade.SessionAgent.dll'
    foreach ($name in @('OpenBlade.SessionAgent.dll', 'OpenBlade.SessionAgent.deps.json',
            'OpenBlade.SessionAgent.runtimeconfig.json', 'Microsoft.WindowsAppRuntime.Insights.Resource.dll',
            'OpenBlade.Tray.pri', 'App.xbf', 'MainWindow.xbf')) {
        New-Item -ItemType File -Path (Join-Path $production.FullName $name) | Out-Null
    }
    Assert-Rejected 'ordinary publish requires the managed Service manifest' {
        Assert-ServicePublishPayload -Payload $production.FullName
    } 'OpenBlade.Service.deps.json'

    $cases | ForEach-Object { Write-Host "PASS $_" }
    Write-Host "$($cases.Count) candidate and production-boundary checks passed."
}
finally {
    # The temporary root was exclusively created above and owns all test data.
    Remove-ValidatedPublishPath -Path $root -PermittedRoot ([IO.Path]::GetTempPath())
}
