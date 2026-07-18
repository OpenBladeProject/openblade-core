[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runner = Join-Path $repository 'captures\tools\Invoke-InteractiveUsbPcapCapture.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Wait-ForFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }
        Start-Sleep -Milliseconds 25
    }

    throw "Timed out waiting for $Path."
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if (($Value.Length -gt 0) -and ($Value -notmatch '[\s"]')) {
        return $Value
    }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Start-Runner {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell.exe'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $allArguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $runner) + $Arguments
    $startInfo.Arguments = (($allArguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Could not start the interactive capture runner test process.'
    }
    return $process
}

function Complete-Runner {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    try {
        $standardOutput = $Process.StandardOutput.ReadToEnd()
        $standardError = $Process.StandardError.ReadToEnd()
        $Process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $Process.ExitCode
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    }
    finally {
        $Process.Dispose()
    }
}

Assert-True (Test-Path -LiteralPath $runner -PathType Leaf) 'The interactive capture runner is missing.'
$runnerSource = Get-Content -Raw -LiteralPath $runner
Assert-True ($runnerSource -notmatch '(?i)GenerateConsoleCtrlEvent|CTRL_C_EVENT') `
    'The runner must never signal a console or use CTRL_C_EVENT directly.'
Assert-True ($runnerSource -notmatch '(?i)-ForceFallback') `
    'The interactive runner must never enable the force-stop fallback.'
Assert-True ($runnerSource -match '(?i)processGroupId\s*-eq\s*0') `
    'The interactive runner must reject process group zero.'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('OpenBlade.InteractiveCapture.Tests.' + [Guid]::NewGuid().ToString('N'))
$activeProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $fakeStart = Join-Path $testRoot 'Fake-Start.ps1'
    $fakeZeroGroupStart = Join-Path $testRoot 'Fake-ZeroGroupStart.ps1'
    $fakeStop = Join-Path $testRoot 'Fake-Stop.ps1'
    $fakeUsbPcap = Join-Path $testRoot 'USBPcapCMD.exe'

    [IO.File]::WriteAllBytes($fakeUsbPcap, [byte[]]@(0))
    [IO.File]::WriteAllText($fakeStart, @'
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string[]]$ArgumentList,
    [string]$OutputPath,
    [string]$SessionPath
)
$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllText("$SessionPath.start.json", ($ArgumentList | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($OutputPath, 'pcap-started', [Text.UTF8Encoding]::new($false))
$session = [ordered]@{
    schemaVersion = 1
    processId = [uint32]$PID
    processGroupId = [uint32]$PID
    executablePath = [IO.Path]::GetFullPath($ExecutablePath)
    processStartTimeUtcTicks = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks
    outputPath = [IO.Path]::GetFullPath($OutputPath)
}
[IO.File]::WriteAllText($SessionPath, ($session | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ ProcessId = [uint32]$PID; ProcessGroupId = [uint32]$PID; OutputPath = $OutputPath; SessionPath = $SessionPath }
'@, [Text.UTF8Encoding]::new($false))

    [IO.File]::WriteAllText($fakeStop, @'
[CmdletBinding()]
param([string]$SessionPath)
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('ForceFallback')) { throw 'Force fallback was passed.' }
[IO.File]::AppendAllText("$SessionPath.stop.log", "graceful`n", [Text.UTF8Encoding]::new($false))
Remove-Item -LiteralPath $SessionPath -Force
[pscustomobject]@{ ProcessId = [uint32]$PID; OutputPath = ''; StopMode = 'Graceful' }
'@, [Text.UTF8Encoding]::new($false))

    $zeroGroupStartSource = [IO.File]::ReadAllText($fakeStart).Replace(
        '[pscustomobject]@{ ProcessId = [uint32]$PID; ProcessGroupId = [uint32]$PID; OutputPath = $OutputPath; SessionPath = $SessionPath }',
        '[pscustomobject]@{ ProcessId = [uint32]$PID; ProcessGroupId = [uint32]0; OutputPath = $OutputPath; SessionPath = $SessionPath }')
    [IO.File]::WriteAllText($fakeZeroGroupStart, $zeroGroupStartSource, [Text.UTF8Encoding]::new($false))

    $successDirectory = Join-Path $testRoot 'success-address'
    $successArguments = @(
        '-UsbPcapDevice', '\\.\USBPcap3',
        '-DeviceAddress', '2',
        '-OutputDirectory', $successDirectory,
        '-TimeoutSeconds', '5',
        '-StartHelperPath', $fakeStart,
        '-StopHelperPath', $fakeStop,
        '-UsbPcapExecutablePath', $fakeUsbPcap,
        '-SkipAdministratorCheck',
        '-SkipServiceManagement')
    $successProcess = Start-Runner -Arguments $successArguments
    [void]$activeProcesses.Add($successProcess)

    $successReadyPath = Join-Path $successDirectory 'capture.ready.json'
    $successStatePath = Join-Path $successDirectory 'capture.state.json'
    $successStopPath = Join-Path $successDirectory 'capture.stop'
    Start-Sleep -Milliseconds 250
    if ($successProcess.HasExited) {
        $earlyResult = Complete-Runner -Process $successProcess
        [void]$activeProcesses.Remove($successProcess)
        throw "Address-mode runner exited before readiness: $($earlyResult.StandardOutput) $($earlyResult.StandardError)"
    }
    Wait-ForFile -Path $successReadyPath
    $ready = Get-Content -Raw -LiteralPath $successReadyPath | ConvertFrom-Json
    $capturingState = Get-Content -Raw -LiteralPath $successStatePath | ConvertFrom-Json
    Assert-True ($ready.status -ceq 'Ready') 'The runner did not publish a Ready marker.'
    Assert-True ($ready.captureMode -ceq 'DeviceAddress') 'The ready marker lost address mode.'
    Assert-True ($ready.deviceAddress -eq 2) 'The ready marker lost the requested address.'
    Assert-True ($ready.processGroupId -gt 0 -and $ready.processGroupId -eq $ready.processId) `
        'The ready marker did not publish a nonzero PID-rooted process group.'
    Assert-True ($capturingState.status -ceq 'Capturing') `
        'The state was not Capturing when readiness was published.'
    [IO.File]::WriteAllText($successStopPath, 'stop', [Text.UTF8Encoding]::new($false))
    $successResult = Complete-Runner -Process $successProcess
    [void]$activeProcesses.Remove($successProcess)
    Assert-True ($successResult.ExitCode -eq 0) `
        "Address-mode runner failed: $($successResult.StandardOutput) $($successResult.StandardError)"
    $completedState = Get-Content -Raw -LiteralPath $successStatePath | ConvertFrom-Json
    Assert-True ($completedState.status -ceq 'Completed') 'The final state was not Completed.'
    Assert-True ($completedState.terminationReason -ceq 'StopSentinel') `
        'The final state did not record the stop sentinel.'
    Assert-True ($completedState.stopMode -ceq 'Graceful') 'The final state did not record graceful shutdown.'
    Assert-True ($completedState.errors.Count -eq 0) 'The completed state unexpectedly contains errors.'
    Assert-True (@(Get-ChildItem -LiteralPath $successDirectory -Filter '*.tmp').Count -eq 0) `
        'An atomic-state temporary file was left behind.'
    Assert-True (@(Get-ChildItem -LiteralPath $successDirectory -Filter '*.bak').Count -eq 0) `
        'An atomic-state backup file was left behind.'
    Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $successDirectory 'capture.usbpcap.json.stop.log')) -eq "graceful`n") `
        'The runner did not invoke the fake graceful stop helper exactly once.'
    $addressArguments = Get-Content -Raw -LiteralPath `
        (Join-Path $successDirectory 'capture.usbpcap.json.start.json') | ConvertFrom-Json
    Assert-True (($addressArguments -join ' ') -match '--devices 2') `
        'Address mode did not pass the requested USB device address.'

    $zeroGroupDirectory = Join-Path $testRoot 'zero-group'
    $zeroGroupProcess = Start-Runner -Arguments @(
        '-UsbPcapDevice', '\\.\USBPcap3',
        '-DeviceAddress', '2',
        '-OutputDirectory', $zeroGroupDirectory,
        '-TimeoutSeconds', '5',
        '-StartHelperPath', $fakeZeroGroupStart,
        '-StopHelperPath', $fakeStop,
        '-UsbPcapExecutablePath', $fakeUsbPcap,
        '-SkipAdministratorCheck',
        '-SkipServiceManagement')
    [void]$activeProcesses.Add($zeroGroupProcess)
    $zeroGroupResult = Complete-Runner -Process $zeroGroupProcess
    [void]$activeProcesses.Remove($zeroGroupProcess)
    Assert-True ($zeroGroupResult.ExitCode -ne 0) 'The runner accepted process group zero.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $zeroGroupDirectory 'capture.ready.json'))) `
        'The runner published readiness for process group zero.'
    $zeroGroupState = Get-Content -Raw -LiteralPath `
        (Join-Path $zeroGroupDirectory 'capture.state.json') | ConvertFrom-Json
    Assert-True ($zeroGroupState.status -ceq 'Failed') 'The zero-group rejection was not recorded as Failed.'
    Assert-True (($zeroGroupState.errors -join ' ') -match 'nonzero PID-rooted process group') `
        'The zero-group failure did not explain the ownership violation.'
    Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $zeroGroupDirectory 'capture.usbpcap.json.stop.log')) -eq "graceful`n") `
        'The zero-group rejection did not clean up through the graceful stop helper.'

    $allDirectory = Join-Path $testRoot 'success-all'
    $allProcess = Start-Runner -Arguments @(
        '-UsbPcapDevice', '\\.\USBPcap3',
        '-AllDevices',
        '-OutputDirectory', $allDirectory,
        '-TimeoutSeconds', '5',
        '-StartHelperPath', $fakeStart,
        '-StopHelperPath', $fakeStop,
        '-UsbPcapExecutablePath', $fakeUsbPcap,
        '-SkipAdministratorCheck',
        '-SkipServiceManagement')
    [void]$activeProcesses.Add($allProcess)
    Wait-ForFile -Path (Join-Path $allDirectory 'capture.ready.json')
    [IO.File]::WriteAllText((Join-Path $allDirectory 'capture.stop'), 'stop', [Text.UTF8Encoding]::new($false))
    $allResult = Complete-Runner -Process $allProcess
    [void]$activeProcesses.Remove($allProcess)
    Assert-True ($allResult.ExitCode -eq 0) `
        "All-devices runner failed: $($allResult.StandardOutput) $($allResult.StandardError)"
    $allState = Get-Content -Raw -LiteralPath (Join-Path $allDirectory 'capture.state.json') | ConvertFrom-Json
    Assert-True ($allState.captureMode -ceq 'AllDevices') 'All-devices mode was not recorded.'
    Assert-True ($null -eq $allState.deviceAddress) 'All-devices mode unexpectedly recorded an address.'
    $allArguments = Get-Content -Raw -LiteralPath `
        (Join-Path $allDirectory 'capture.usbpcap.json.start.json') | ConvertFrom-Json
    Assert-True (($allArguments -join ' ') -notmatch '(?:^| )--devices(?: |$)') `
        'All-devices mode unexpectedly filtered the capture by address.'
    Assert-True (($allArguments -join ' ') -match '(?:^| )-A(?: |$)') `
        'All-devices mode did not request USBPcap capture from all devices.'

    $timeoutDirectory = Join-Path $testRoot 'timeout'
    $timeoutProcess = Start-Runner -Arguments @(
        '-UsbPcapDevice', '\\.\USBPcap3',
        '-AllDevices',
        '-OutputDirectory', $timeoutDirectory,
        '-TimeoutSeconds', '1',
        '-StartHelperPath', $fakeStart,
        '-StopHelperPath', $fakeStop,
        '-UsbPcapExecutablePath', $fakeUsbPcap,
        '-SkipAdministratorCheck',
        '-SkipServiceManagement')
    [void]$activeProcesses.Add($timeoutProcess)
    Wait-ForFile -Path (Join-Path $timeoutDirectory 'capture.ready.json')
    $timeoutResult = Complete-Runner -Process $timeoutProcess
    [void]$activeProcesses.Remove($timeoutProcess)
    Assert-True ($timeoutResult.ExitCode -ne 0) 'The bounded-timeout run unexpectedly succeeded.'
    $timeoutState = Get-Content -Raw -LiteralPath (Join-Path $timeoutDirectory 'capture.state.json') | ConvertFrom-Json
    Assert-True ($timeoutState.status -ceq 'TimedOut') 'The timeout state was not TimedOut.'
    Assert-True ($timeoutState.terminationReason -ceq 'Timeout') 'The timeout reason was not recorded.'
    Assert-True ($timeoutState.stopMode -ceq 'Graceful') 'The timed-out capture was not stopped gracefully.'
    Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $timeoutDirectory 'capture.usbpcap.json.stop.log')) -eq "graceful`n") `
        'The timed-out capture did not use the graceful stop helper.'

    Write-Host 'Interactive USBPcap capture-runner tests passed.'
}
finally {
    foreach ($process in @($activeProcesses)) {
        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        finally {
            $process.Dispose()
        }
    }

    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTestRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected test path $resolvedTestRoot."
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
