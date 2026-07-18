[CmdletBinding(DefaultParameterSetName = 'DeviceAddress')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UsbPcapDevice,

    [Parameter(Mandatory, ParameterSetName = 'DeviceAddress')]
    [ValidateRange(1, 127)]
    [int]$DeviceAddress,

    [Parameter(Mandatory, ParameterSetName = 'AllDevices')]
    [switch]$AllDevices,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 600,

    [Parameter(DontShow)]
    [string]$StartHelperPath,

    [Parameter(DontShow)]
    [string]$StopHelperPath,

    [Parameter(DontShow)]
    [string]$UsbPcapExecutablePath = 'C:\Program Files\USBPcap\USBPcapCMD.exe',

    [Parameter(DontShow)]
    [switch]$SkipAdministratorCheck,

    [Parameter(DontShow)]
    [switch]$SkipServiceManagement
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally {
        $identity.Dispose()
    }
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value,
        [switch]$CreateOnly
    )

    $json = $Value | ConvertTo-Json -Depth 10
    $temporaryPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if ($CreateOnly) {
            [IO.File]::Move($temporaryPath, $Path)
        }
        elseif (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
            Remove-Item -LiteralPath $backupPath -Force
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

if ([string]::IsNullOrWhiteSpace($StartHelperPath)) {
    $StartHelperPath = Join-Path $PSScriptRoot 'Start-UsbPcapCapture.ps1'
}
if ([string]::IsNullOrWhiteSpace($StopHelperPath)) {
    $StopHelperPath = Join-Path $PSScriptRoot 'Stop-UsbPcapCapture.ps1'
}

$resolvedStartHelper = [IO.Path]::GetFullPath($StartHelperPath)
$resolvedStopHelper = [IO.Path]::GetFullPath($StopHelperPath)
$resolvedUsbPcapExecutable = [IO.Path]::GetFullPath($UsbPcapExecutablePath)
$pcapPath = Join-Path $resolvedOutputDirectory 'capture.pcap'
$sessionPath = Join-Path $resolvedOutputDirectory 'capture.usbpcap.json'
$readyPath = Join-Path $resolvedOutputDirectory 'capture.ready.json'
$stopSentinelPath = Join-Path $resolvedOutputDirectory 'capture.stop'
$statePath = Join-Path $resolvedOutputDirectory 'capture.state.json'
$captureMode = if ($AllDevices) { 'AllDevices' } else { 'DeviceAddress' }
$requestedDeviceAddress = if ($AllDevices) { $null } else { $DeviceAddress }
$startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')

foreach ($outputPath in @($pcapPath, $sessionPath, $readyPath, $stopSentinelPath, $statePath)) {
    if (Test-Path -LiteralPath $outputPath) {
        throw "Refusing to overwrite existing capture artifact: $outputPath"
    }
}

$state = [ordered]@{
    schemaVersion = 1
    status = 'Initializing'
    startedAtUtc = $startedAtUtc
    updatedAtUtc = $startedAtUtc
    completedAtUtc = $null
    usbPcapDevice = $UsbPcapDevice
    captureMode = $captureMode
    deviceAddress = $requestedDeviceAddress
    timeoutSeconds = $TimeoutSeconds
    processId = $null
    processGroupId = $null
    pcapPath = $pcapPath
    sessionPath = $sessionPath
    readyPath = $readyPath
    stopSentinelPath = $stopSentinelPath
    terminationReason = $null
    stopMode = $null
    service = [ordered]@{
        name = 'OpenBlade'
        managementSkipped = [bool]$SkipServiceManagement
        wasRunning = $false
        stopped = $false
        restarted = $false
    }
    errors = @()
}

function Update-CaptureState {
    param([Parameter(Mandatory)][string]$Status)

    $state.status = $Status
    $state.updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Write-AtomicJson -Path $statePath -Value $state
}

Write-AtomicJson -Path $statePath -Value $state -CreateOnly

$captureStarted = $false
$serviceWasRunning = $false
$timedOut = $false
$errors = [Collections.Generic.List[string]]::new()

try {
    if (-not $SkipAdministratorCheck -and -not (Test-IsAdministrator)) {
        throw 'This capture runner must be started from an elevated administrator process.'
    }

    foreach ($requiredPath in @($resolvedStartHelper, $resolvedStopHelper, $resolvedUsbPcapExecutable)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required capture component was not found: $requiredPath"
        }
    }
    if ([IO.Path]::GetFileName($resolvedUsbPcapExecutable) -ine 'USBPcapCMD.exe') {
        throw "Refusing to use an unexpected capture executable: $resolvedUsbPcapExecutable"
    }
    if (Get-Process -Name 'USBPcapCMD' -ErrorAction SilentlyContinue) {
        throw 'Refusing to start while another USBPcapCMD process is active.'
    }

    if (-not $SkipServiceManagement) {
        $service = Get-Service -Name 'OpenBlade' -ErrorAction Stop
        $serviceWasRunning = $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running
        $state.service.wasRunning = $serviceWasRunning
        if ($serviceWasRunning) {
            Stop-Service -Name 'OpenBlade' -ErrorAction Stop
            (Get-Service -Name 'OpenBlade').WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(15))
            $state.service.stopped = $true
        }
    }

    $arguments = @('-d', $UsbPcapDevice)
    if ($AllDevices) {
        $arguments += '-A'
    }
    else {
        $arguments += @(
            '--devices',
            $DeviceAddress.ToString([Globalization.CultureInfo]::InvariantCulture))
    }
    $arguments += @('--inject-descriptors', '-s', '65535', '-o', $pcapPath)

    $capture = & $resolvedStartHelper `
        -ExecutablePath $resolvedUsbPcapExecutable `
        -ArgumentList $arguments `
        -OutputPath $pcapPath `
        -SessionPath $sessionPath
    if ($capture -is [array]) {
        $capture = $capture | Select-Object -Last 1
    }
    $captureStarted = Test-Path -LiteralPath $sessionPath -PathType Leaf

    $captureProcessId = [uint32]$capture.ProcessId
    $processGroupId = [uint32]$capture.ProcessGroupId
    if ($captureProcessId -eq 0 -or $processGroupId -eq 0 -or
        $processGroupId -ne $captureProcessId) {
        throw 'The capture launcher did not return a nonzero PID-rooted process group.'
    }
    if (-not (Test-Path -LiteralPath $sessionPath -PathType Leaf)) {
        throw 'The capture launcher did not publish its ownership session.'
    }
    $captureProcess = Get-Process -Id $captureProcessId -ErrorAction Stop
    $captureProcess.Dispose()
    $state.processId = $captureProcessId
    $state.processGroupId = $processGroupId
    Update-CaptureState -Status 'Capturing'

    $ready = [ordered]@{
        schemaVersion = 1
        status = 'Ready'
        readyAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        usbPcapDevice = $UsbPcapDevice
        captureMode = $captureMode
        deviceAddress = $requestedDeviceAddress
        processId = $captureProcessId
        processGroupId = $processGroupId
        pcapPath = $pcapPath
        statePath = $statePath
        stopSentinelPath = $stopSentinelPath
    }
    Write-AtomicJson -Path $readyPath -Value $ready -CreateOnly

    Write-Host "USBPcap capture is ready. Perform the interactive action now."
    Write-Host "To stop safely, create this sentinel: $stopSentinelPath"

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $stopSentinelPath -PathType Leaf)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $timedOut = $true
            $state.terminationReason = 'Timeout'
            break
        }

        $runningCapture = Get-Process -Id $captureProcessId -ErrorAction SilentlyContinue
        if ($null -eq $runningCapture) {
            throw "USBPcap process $captureProcessId exited before a stop was requested."
        }
        $runningCapture.Dispose()
        Start-Sleep -Milliseconds 100
    }

    if (-not $timedOut) {
        $state.terminationReason = 'StopSentinel'
    }
    Update-CaptureState -Status 'Stopping'
}
catch {
    $failure = "$($_.Exception.GetType().FullName): $($_.Exception.Message)"
    if (-not [string]::IsNullOrWhiteSpace($_.ScriptStackTrace)) {
        $failure += "`n$($_.ScriptStackTrace)"
    }
    [void]$errors.Add($failure)
}
finally {
    if ($captureStarted -and (Test-Path -LiteralPath $sessionPath -PathType Leaf)) {
        try {
            $stopResult = & $resolvedStopHelper -SessionPath $sessionPath
            if ($stopResult -is [array]) {
                $stopResult = $stopResult | Select-Object -Last 1
            }
            if ([string]$stopResult.StopMode -cne 'Graceful') {
                throw "The capture stop helper returned an unexpected stop mode: $($stopResult.StopMode)"
            }
            $state.stopMode = 'Graceful'
        }
        catch {
            [void]$errors.Add("Capture shutdown failed: $($_.Exception.Message)")
        }
    }

    if (-not $SkipServiceManagement -and $serviceWasRunning) {
        try {
            Start-Service -Name 'OpenBlade' -ErrorAction Stop
            (Get-Service -Name 'OpenBlade').WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(15))
            $state.service.restarted = $true
        }
        catch {
            [void]$errors.Add("OpenBlade service restart failed: $($_.Exception.Message)")
        }
    }

    $state.errors = @($errors)
    $state.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    if ($errors.Count -gt 0) {
        Update-CaptureState -Status 'Failed'
    }
    elseif ($timedOut) {
        Update-CaptureState -Status 'TimedOut'
    }
    else {
        Update-CaptureState -Status 'Completed'
    }
}

if ($errors.Count -gt 0) {
    throw ($errors -join [Environment]::NewLine)
}
if ($timedOut) {
    throw "The capture reached its bounded timeout of $TimeoutSeconds seconds. USBPcap was stopped gracefully."
}

[pscustomobject]@{
    Status = $state.status
    PcapPath = $pcapPath
    StatePath = $statePath
    StopMode = $state.stopMode
}
