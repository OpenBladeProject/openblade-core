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
    $script:downloadFailures = [Collections.Generic.Queue[int]]::new()
    $script:retrySleeps = [Collections.Generic.List[int]]::new()
    Add-Type -TypeDefinition @'
public sealed class PawnIoTestResponse : System.Net.WebResponse
{
    public int StatusCode { get; set; }
}
'@
    function Start-Sleep { param([int]$Seconds) $script:retrySleeps.Add($Seconds) }
    function Get-FileHash { param($LiteralPath, $Algorithm) @{ Hash = $(if ($script:badHash) { 'bad' } else { (Get-OpenBladePawnIoPrerequisite).Sha256 }) } }
    function Get-AuthenticodeSignature { param($LiteralPath) @{ Status = [Management.Automation.SignatureStatus]::Valid;
        SignerCertificate = @{ Subject = $(if ($script:badSigner) { 'other publisher' } else { (Get-OpenBladePawnIoPrerequisite).Publisher }) } } }
    function Invoke-WebRequest {
        param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec, $Headers)
        if ($Uri -ne 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/assets/374375077' -or
            $Headers.Accept -ne 'application/octet-stream' -or $Headers['X-GitHub-Api-Version'] -ne '2022-11-28' -or
            -not $UseBasicParsing -or $TimeoutSec -ne 60) {
            throw 'PawnIO acquisition did not request the exact official binary asset with a bounded download.'
        }
        $script:downloads++
        if (Test-Path -LiteralPath $OutFile) { throw 'A download retry retained partial bytes.' }
        [IO.File]::WriteAllText($OutFile, 'fake installer bytes')
        if ($script:downloadFailures.Count -gt 0) {
            $status = $script:downloadFailures.Dequeue()
            if ($status -eq -1) { throw [Net.WebException]::new('simulated timeout', [Net.WebExceptionStatus]::Timeout) }
            if ($status -eq -2) { throw [InvalidOperationException]::new('simulated unexpected failure') }
            $response = [PawnIoTestResponse]::new()
            $response.StatusCode = $status
            throw [Net.WebException]::new("simulated HTTP $status", $null, [Net.WebExceptionStatus]::ProtocolError, $response)
        }
    }
    $cache = Join-Path $root 'cache'
    [void](Copy-OpenBladePawnIoPrerequisite -DestinationDirectory (Join-Path $root 'download1') -CacheDirectory $cache)
    [void](Copy-OpenBladePawnIoPrerequisite -DestinationDirectory (Join-Path $root 'download2') -CacheDirectory $cache)
    if ($script:downloads -ne 1) { throw 'A verified PawnIO cache entry was downloaded again.' }
    $caseNumber = 0
    foreach ($case in @(
        @{ Failures = @(503, 503); Requests = 3; Sleeps = @(5, 15); Success = $true },
        @{ Failures = @(-1); Requests = 2; Sleeps = @(5); Success = $true },
        @{ Failures = @(503, 503, 503, 503); Requests = 4; Sleeps = @(5, 15, 30); Success = $false },
        @{ Failures = @(404); Requests = 1; Sleeps = @(); Success = $false },
        @{ Failures = @(403); Requests = 1; Sleeps = @(); Success = $false },
        @{ Failures = @(-2); Requests = 1; Sleeps = @(); Success = $false }
    )) {
        $caseNumber++
        $caseRoot = Join-Path $root "retry-$caseNumber"
        $script:downloads = 0
        $script:retrySleeps.Clear()
        $script:downloadFailures.Clear()
        foreach ($status in $case.Failures) { $script:downloadFailures.Enqueue($status) }
        $succeeded = $false
        $failureMessage = ''
        try {
            [void](Copy-OpenBladePawnIoPrerequisite -DestinationDirectory (Join-Path $caseRoot 'payload') -CacheDirectory (Join-Path $caseRoot 'cache'))
            $succeeded = $true
        }
        catch { $failureMessage = $_.Exception.Message }
        if ($succeeded -ne $case.Success -or $script:downloads -ne $case.Requests -or
            ($script:retrySleeps -join ',') -ne ($case.Sleeps -join ',')) {
            throw "Unexpected download retry result in case ${caseNumber}: requests=$script:downloads; success=$succeeded; error=$failureMessage"
        }
        if (@(Get-ChildItem -LiteralPath $caseRoot -Recurse -Filter '*.download').Count -ne 0) {
            throw 'A completed or failed acquisition retained temporary download bytes.'
        }
        if (-not $succeeded -and ($failureMessage -notlike '*simulated*' -or
            @(Get-ChildItem -LiteralPath $caseRoot -Recurse -Filter 'PawnIO_setup.exe').Count -ne 0)) {
            throw 'A failed acquisition lost its error or published a cache/payload entry.'
        }
    }
    foreach ($failure in @('hash', 'signer')) {
        $script:downloadFailures.Clear()
        $script:downloads = 0
        $script:retrySleeps.Clear()
        $script:badHash = $failure -eq 'hash'
        $script:badSigner = $failure -eq 'signer'
        $rejected = $false
        $caseRoot = Join-Path $root "bad-$failure"
        try { [void](Copy-OpenBladePawnIoPrerequisite -DestinationDirectory (Join-Path $caseRoot 'payload') -CacheDirectory (Join-Path $caseRoot 'cache')) }
        catch { $rejected = $true }
        if (-not $rejected -or $script:downloads -ne 1 -or $script:retrySleeps.Count -ne 0 -or
            @(Get-ChildItem -LiteralPath $caseRoot -Recurse -File).Count -ne 0) {
            throw "PawnIO acquisition accepted, retried or retained a bad $failure."
        }
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
