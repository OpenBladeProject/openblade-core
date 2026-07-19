[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CaptureExecutablePath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$captureExecutable = [IO.Path]::GetFullPath($CaptureExecutablePath)
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Path]::GetFileName($captureExecutable) -ine 'OpenBlade.Capture.exe') {
    throw 'The capture executable must be OpenBlade.Capture.exe.'
}
if (-not [IO.File]::Exists($captureExecutable)) {
    throw "Capture executable not found: $captureExecutable"
}

[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$resultPath = Join-Path $outputDirectory 'query.json'
$statusPath = Join-Path $outputDirectory 'status.json'
$service = Get-Service -Name 'OpenBlade' -ErrorAction Stop
$restartService = $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running
$exitCode = $null
$failure = $null

try {
    if ($restartService) {
        Stop-Service -Name 'OpenBlade' -Force -ErrorAction Stop
        (Get-Service -Name 'OpenBlade').WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(15))
    }

    $output = & $captureExecutable 'query-performance-fan-discriminator' 2>&1 |
        Out-String
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllText(
        $resultPath,
        $output,
        [Text.UTF8Encoding]::new($false))
    if ($exitCode -ne 0) {
        throw "Read-only performance query exited with code $exitCode."
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($restartService) {
        try {
            Start-Service -Name 'OpenBlade' -ErrorAction Stop
            (Get-Service -Name 'OpenBlade').WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(15))
        }
        catch {
            $restartFailure = $_.Exception.Message
            $failure = if ($failure) {
                "$failure Service restart also failed: $restartFailure"
            }
            else {
                "Service restart failed: $restartFailure"
            }
        }
    }

    $status = [ordered]@{
        schemaVersion = 1
        readOnly = $true
        serviceWasRunning = $restartService
        serviceRestarted = (Get-Service -Name 'OpenBlade').Status -eq
            [ServiceProcess.ServiceControllerStatus]::Running
        exitCode = $exitCode
        resultPath = $resultPath
        failure = $failure
    }
    [IO.File]::WriteAllText(
        $statusPath,
        ($status | ConvertTo-Json -Depth 3),
        [Text.UTF8Encoding]::new($false))
}

if ($failure) {
    Write-Error $failure
    exit 1
}

exit 0
