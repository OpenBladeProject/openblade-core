[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build/PawnIoPrerequisite.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.PawnIoPayload.' + [Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($root)
    $helper = Join-Path $root 'helper'
    $payload = Join-Path $root 'payload'
    [void][IO.Directory]::CreateDirectory($helper)
    [void][IO.Directory]::CreateDirectory($payload)
    foreach ($name in @('OpenBlade.CpuTemperature.exe', 'OpenBlade.CpuTemperature.dll',
        'OpenBlade.CpuTemperature.deps.json', 'OpenBlade.CpuTemperature.runtimeconfig.json', 'LibreHardwareMonitorLib.dll')) {
        [IO.File]::WriteAllText((Join-Path $helper $name), 'helper')
    }
    [IO.File]::WriteAllText((Join-Path $helper 'OpenBlade.Platform.dll'), 'shared')
    [IO.File]::WriteAllText((Join-Path $payload 'OpenBlade.Platform.dll'), 'conflicting')
    $rejected = $false
    try { Add-CpuTemperaturePublishPayload -Helper $helper -Payload $payload }
    catch { $rejected = $_.Exception.Message -like '*shared dependency differs*' }
    if (-not $rejected -or @(Get-ChildItem -LiteralPath $payload).Count -ne 1) { throw 'Conflicting helper dependencies changed the main payload.' }
    [IO.File]::WriteAllText((Join-Path $payload 'OpenBlade.Platform.dll'), 'shared')
    Add-CpuTemperaturePublishPayload -Helper $helper -Payload $payload
    if (@(Get-ChildItem -LiteralPath $payload).Count -ne 6) { throw 'CPU helper payload merge omitted a required file.' }

    # Build acquisition is mocked; the test never downloads or executes vendor code.
    $script:downloads = 0
    $script:badHash = $false
    $script:badSigner = $false
    function Get-FileHash { param($LiteralPath, $Algorithm) @{ Hash = $(if ($script:badHash) { 'bad' } else { (Get-OpenBladePawnIoPrerequisite).Sha256 }) } }
    function Get-AuthenticodeSignature { param($LiteralPath) @{ Status = [Management.Automation.SignatureStatus]::Valid;
        SignerCertificate = @{ Subject = $(if ($script:badSigner) { 'other publisher' } else { (Get-OpenBladePawnIoPrerequisite).Publisher }) } } }
    function Invoke-WebRequest { param($Uri, $OutFile) $script:downloads++; [IO.File]::WriteAllText($OutFile, 'fake installer bytes') }
    $cache = Join-Path $root 'cache'
    [void](Copy-OpenBladePawnIoPrerequisite -DestinationDirectory (Join-Path $root 'download1') -CacheDirectory $cache)
    [void](Copy-OpenBladePawnIoPrerequisite -DestinationDirectory (Join-Path $root 'download2') -CacheDirectory $cache)
    if ($script:downloads -ne 1) { throw 'A verified PawnIO cache entry was downloaded again.' }
    foreach ($failure in @('hash', 'signer')) {
        $script:badHash = $failure -eq 'hash'
        $script:badSigner = $failure -eq 'signer'
        $rejected = $false
        try { Assert-OpenBladePawnIoPrerequisite -Path 'unused-mocked-path' } catch { $rejected = $true }
        if (-not $rejected) { throw "PawnIO acquisition accepted a bad $failure." }
    }
    $source = Join-Path $root 'repo\third_party\LibreHardwareMonitor'
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'obj'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'repo\build\third-party'))
    [IO.File]::WriteAllText((Join-Path $source 'LICENSE'), 'source license')
    [IO.File]::WriteAllText((Join-Path $source 'Reviewed.cs'), 'corresponding modified source')
    [IO.File]::WriteAllText((Join-Path $source 'obj\private.txt'), 'build output')
    [IO.File]::WriteAllText((Join-Path $root 'repo\build\third-party\PawnIO-NOTICE.txt'), 'vendor notice')
    Add-CpuTemperatureLicensePayload -Repository (Join-Path $root 'repo') -Payload $payload
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $payload 'licenses\LibreHardwareMonitor-source.zip'))
    try {
        $names = @($zip.Entries.FullName)
        if ('LICENSE' -notin $names -or 'Reviewed.cs' -notin $names -or 'obj/private.txt' -in $names) {
            throw 'Source distribution omitted corresponding source/license or included private build output.'
        }
    }
    finally { $zip.Dispose() }
    Write-Output 'PawnIO prerequisite/payload tests passed (mock acquisition only).'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $expected = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\OpenBlade.PawnIoPayload.'
    if (-not $resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary test root.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
