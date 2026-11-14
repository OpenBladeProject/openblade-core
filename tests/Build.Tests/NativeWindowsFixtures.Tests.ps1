[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
& $python -B (Join-Path $repository 'build/test_native_windows_fixtures.py')
if ($LASTEXITCODE -ne 0) { throw "Native Windows fixture harness contracts failed: $LASTEXITCODE" }

. (Join-Path $repository 'build/NativeWindowsFixtures.ps1')
foreach ($case in @(
    @{ Bytes = 24GB; Policy = 'Stop'; Reads = 1 },
    @{ Bytes = 25GB; Policy = 'Warn'; Reads = 2 },
    @{ Bytes = 49GB; Policy = 'Warn'; Reads = 2 },
    @{ Bytes = 50GB; Policy = 'Ready'; Reads = 1 }
)) {
    if ((Get-OpenBladeNativeFixtureDiskPolicy $case.Bytes) -ne $case.Policy) { throw 'Disk threshold policy mismatch.' }
    $script:fixtureDiskReadCount = 0
    $script:fixtureDiskBytes = $case.Bytes
    $threw = $false
    try {
        $messages = @(Assert-OpenBladeNativeFixtureDiskSpace -ReadAvailableBytes {
            $script:fixtureDiskReadCount++
            return $script:fixtureDiskBytes
        } 3>&1)
    }
    catch { $threw = $true }
    if ($threw -ne ($case.Policy -eq 'Stop') -or $script:fixtureDiskReadCount -ne $case.Reads) { throw 'Disk threshold admission mismatch.' }
    if ($case.Policy -eq 'Warn' -and @($messages).Count -ne 1) { throw 'Low disk warning was not reported.' }
}
$script:fixtureDiskReadCount = 0
$threw = $false
try {
    $null = Assert-OpenBladeNativeFixtureDiskSpace -ReadAvailableBytes {
        $script:fixtureDiskReadCount++
        if ($script:fixtureDiskReadCount -eq 1) { return 30GB }
        return 24GB
    } 3>&1
}
catch { $threw = $true }
if (-not $threw -or $script:fixtureDiskReadCount -ne 2) { throw 'Disk recheck must stop below 25 GiB.' }
Write-Host 'Fixture disk threshold contracts passed.'
