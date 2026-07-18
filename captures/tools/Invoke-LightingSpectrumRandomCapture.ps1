[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UsbPcapDevice,

    [Parameter(Mandatory)]
    [ValidateRange(1, 127)]
    [int]$DeviceAddress,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(DontShow)]
    [string]$RunnerPath,

    [Parameter(DontShow)]
    [string]$PlanPath,

    [Parameter(DontShow)]
    [string]$UsbPcapExecutablePath = 'C:\Program Files\USBPcap\USBPcapCMD.exe',

    [Parameter(DontShow)]
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe',

    [Parameter(DontShow)]
    [ValidateRange(1, 60)]
    [int]$ReadyTimeoutSeconds = 60,

    [switch]$FileControlled,

    [string]$PreCaptureSetting,

    [string]$FileControlDirectory,

    [Parameter(DontShow)]
    [string]$ApprovalPhrase,

    [Parameter(DontShow)]
    [ValidateRange(10, 300)]
    [int]$ActionTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if (($Value.Length -gt 0) -and ($Value -notmatch '[\s"]')) {
        return $Value
    }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value
    )

    $temporaryPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        $json = $Value | ConvertTo-Json -Depth 20
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
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

function Wait-ForCaptureReady {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ReadyPath,
        [int]$TimeoutSeconds = 60
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ReadyPath -PathType Leaf) {
            return
        }
        if ($Process.HasExited) {
            throw "The capture runner exited before publishing readiness (exit code $($Process.ExitCode))."
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for the capture runner readiness file: $ReadyPath"
}

function Wait-ForControlSignal {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }
        if ($Process.HasExited) {
            throw "The capture runner exited while waiting for $Description."
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Description at $Path"
}

function Wait-ForObservation {
    param(
        [Parameter(Mandatory)][int]$Seconds,
        [Parameter(Mandatory)][string]$StepId
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(0, [Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
        Write-Progress -Activity "Capturing $StepId" -Status "$remaining seconds remaining" `
            -PercentComplete ([Math]::Min(100, 100 * ($Seconds - $remaining) / $Seconds))
        Start-Sleep -Milliseconds 250
    }
    Write-Progress -Activity "Capturing $StepId" -Completed
}

if ([string]::IsNullOrWhiteSpace($RunnerPath)) {
    $RunnerPath = Join-Path $PSScriptRoot 'Invoke-InteractiveUsbPcapCapture.ps1'
}
if ([string]::IsNullOrWhiteSpace($PlanPath)) {
    $PlanPath = Join-Path (Split-Path -Parent $PSScriptRoot) `
        'plans\2026-07-18-lighting-spectrum-random-oracle.json'
}

$resolvedRunner = (Resolve-Path -LiteralPath $RunnerPath -ErrorAction Stop).Path
$resolvedPlan = (Resolve-Path -LiteralPath $PlanPath -ErrorAction Stop).Path
$resolvedUsbPcapExecutable = (Resolve-Path -LiteralPath $UsbPcapExecutablePath -ErrorAction Stop).Path
$resolvedTshark = (Resolve-Path -LiteralPath $TsharkPath -ErrorAction Stop).Path
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$plan = Get-Content -LiteralPath $resolvedPlan -Raw | ConvertFrom-Json

if ($plan.schemaVersion -ne 1 -or $plan.id -cne 'pid-02e0-lighting-spectrum-random-oracle-v1') {
    throw 'The lighting capture plan has an unsupported identity or schema.'
}
if ($plan.target.modelNumber -cne 'RZ09-0581' -or
    $plan.target.vendorIdHex -cne '1532' -or
    $plan.target.productIdHex -cne '02E0' -or
    $plan.target.bios -cne '3.01') {
    throw 'The lighting capture plan does not describe the exact admitted target.'
}
$steps = @($plan.steps)
$controlledSeconds = ($steps | Measure-Object -Property holdSeconds -Sum).Sum
if ($steps.Count -ne 10 -or $controlledSeconds -ne 2160 -or
    $plan.capture.controlledObservationSeconds -ne $controlledSeconds -or
    $plan.capture.timeoutSeconds -le $controlledSeconds) {
    throw 'The lighting capture plan no longer preserves its reviewed observation windows.'
}
$spectrumSteps = @($steps | Where-Object { $_.id -like 'spectrum-run-*' })
if ($spectrumSteps.Count -ne 3 -or
    @($spectrumSteps | Where-Object { $_.holdSeconds -lt 100 }).Count -ne 0 -or
    @($steps | Where-Object { $_.id -eq 'breathing-random-long' -and $_.holdSeconds -eq 1800 }).Count -ne 1) {
    throw 'The lighting capture plan no longer contains the reviewed Spectrum and Random Breathing windows.'
}
if ([IO.Path]::GetFileName($resolvedRunner) -ine 'Invoke-InteractiveUsbPcapCapture.ps1') {
    throw "Refusing to use an unexpected capture runner: $resolvedRunner"
}
if ([IO.Path]::GetFileName($resolvedUsbPcapExecutable) -ine 'USBPcapCMD.exe') {
    throw "Refusing to use an unexpected capture executable: $resolvedUsbPcapExecutable"
}
if ([IO.Path]::GetFileName($resolvedTshark) -ine 'tshark.exe') {
    throw "Refusing to use an unexpected decoder: $resolvedTshark"
}

[IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null
$readyPath = Join-Path $resolvedOutputDirectory 'capture.ready.json'
$statePath = Join-Path $resolvedOutputDirectory 'capture.state.json'
$stopPath = Join-Path $resolvedOutputDirectory 'capture.stop'
$pcapPath = Join-Path $resolvedOutputDirectory 'capture.pcap'
$annotationPath = Join-Path $resolvedOutputDirectory 'lighting-spectrum-random-annotations.json'
$controlStatePath = Join-Path $resolvedOutputDirectory 'lighting-sequence-control.json'
$controlDirectory = if ([string]::IsNullOrWhiteSpace($FileControlDirectory)) {
    Join-Path $resolvedOutputDirectory 'lighting-sequence-control'
} else {
    [IO.Path]::GetFullPath($FileControlDirectory)
}
foreach ($path in @($readyPath, $statePath, $stopPath, $pcapPath, $annotationPath)) {
    if (Test-Path -LiteralPath $path) {
        throw "Refusing to overwrite an existing capture artifact: $path"
    }
}
if ($FileControlled) {
    if (Test-Path -LiteralPath $controlStatePath) {
        throw 'Refusing to overwrite the existing file-control state.'
    }
    if ([string]::IsNullOrWhiteSpace($FileControlDirectory)) {
        if (Test-Path -LiteralPath $controlDirectory) {
            throw 'Refusing to overwrite the existing file-control directory.'
        }
        [IO.Directory]::CreateDirectory($controlDirectory) | Out-Null
    }
    elseif (-not (Test-Path -LiteralPath $controlDirectory -PathType Container)) {
        throw 'The supplied file-control directory must already exist.'
    }
    elseif (@(Get-ChildItem -LiteralPath $controlDirectory -Force).Count -ne 0) {
        throw 'The supplied file-control directory must be empty.'
    }
}

Write-Host 'This is a Synapse oracle capture, not OpenBlade lighting validation.'
Write-Host 'It will capture 2,160 controlled seconds (36 minutes) inside a 45-minute bounded session.'
Write-Host 'USBPcap will be launched only by the reviewed isolated-process-group runner.'
if ($FileControlled) {
    if ($ApprovalPhrase -cne 'CAPTURE SPECTRUM AND RANDOM BREATHING') {
        throw 'File-controlled capture requires the exact reviewed approval phrase.'
    }
    if ([string]::IsNullOrWhiteSpace($PreCaptureSetting)) {
        throw 'File-controlled capture requires the exact pre-capture lighting description.'
    }
    $preCaptureSetting = $PreCaptureSetting
}
else {
    $confirmation = Read-Host 'Type CAPTURE SPECTRUM AND RANDOM BREATHING to continue'
    if ($confirmation -cne 'CAPTURE SPECTRUM AND RANDOM BREATHING') {
        throw 'Capture confirmation did not match; nothing was started.'
    }
    $preCaptureSetting = Read-Host `
        'Describe the current effect, colors, and brightness exactly so the final step can restore them'
    if ([string]::IsNullOrWhiteSpace($preCaptureSetting)) {
        throw 'A pre-capture lighting description is required for restoration.'
    }
}

$hostExecutable = (Get-Process -Id $PID -ErrorAction Stop).Path
$runnerArguments = @(
    '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', $resolvedRunner,
    '-UsbPcapDevice', $UsbPcapDevice,
    '-DeviceAddress', $DeviceAddress.ToString([Globalization.CultureInfo]::InvariantCulture),
    '-OutputDirectory', $resolvedOutputDirectory,
    '-TimeoutSeconds', $plan.capture.timeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '-UsbPcapExecutablePath', $resolvedUsbPcapExecutable)
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $hostExecutable
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.WorkingDirectory = $resolvedOutputDirectory
$startInfo.Arguments = (($runnerArguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')

$runner = [Diagnostics.Process]::new()
$runner.StartInfo = $startInfo
$runnerStarted = $false
$runnerOutputTask = $null
$runnerErrorTask = $null
$sequenceFailure = $null
$annotations = [ordered]@{
    schemaVersion = 1
    planId = $plan.id
    planSha256 = (Get-FileHash -LiteralPath $resolvedPlan -Algorithm SHA256).Hash
    evidenceProvenance = $plan.evidenceProvenance
    target = $plan.target
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    completedAtUtc = $null
    preCaptureSetting = $preCaptureSetting
    usbPcapDevice = $UsbPcapDevice
    deviceAddress = $DeviceAddress
    captureTimeoutSeconds = $plan.capture.timeoutSeconds
    controlledObservationSeconds = $controlledSeconds
    actions = [Collections.Generic.List[object]]::new()
    pcap = $null
    gracefulStopConfirmed = $false
    decodable = $false
}

try {
    if (-not $runner.Start()) {
        throw 'Could not start the reviewed interactive USBPcap capture runner.'
    }
    $runnerStarted = $true
    $runnerOutputTask = $runner.StandardOutput.ReadToEndAsync()
    $runnerErrorTask = $runner.StandardError.ReadToEndAsync()
    Wait-ForCaptureReady -Process $runner -ReadyPath $readyPath `
        -TimeoutSeconds $ReadyTimeoutSeconds
    Write-AtomicJson -Path $annotationPath -Value $annotations

    for ($stepIndex = 0; $stepIndex -lt $steps.Count; $stepIndex++) {
        $step = $steps[$stepIndex]
        Write-Host ''
        Write-Host "Next: $($step.id)"
        Write-Host $step.instruction
        if ($step.id -ceq 'restore-pre-capture-setting') {
            Write-Host "Recorded pre-capture setting: $preCaptureSetting"
        }
        if ($FileControlled) {
            $controlPrefix = '{0:D2}-{1}' -f ($stepIndex + 1), $step.id
            $startSignalPath = Join-Path $controlDirectory "$controlPrefix.start"
            $confirmedSignalPath = Join-Path $controlDirectory "$controlPrefix.confirmed"
            $controlState = [ordered]@{
                schemaVersion = 1
                status = 'AwaitingActionStart'
                stepIndex = $stepIndex
                stepCount = $steps.Count
                stepId = $step.id
                instruction = $step.instruction
                preCaptureSetting = if ($step.id -ceq 'restore-pre-capture-setting') {
                    $preCaptureSetting
                } else {
                    $null
                }
                startSignalPath = $startSignalPath
                confirmedSignalPath = $confirmedSignalPath
                holdSeconds = $step.holdSeconds
                updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            }
            Write-AtomicJson -Path $controlStatePath -Value $controlState
            Wait-ForControlSignal -Process $runner -Path $startSignalPath `
                -Description "the action-start signal for $($step.id)" `
                -TimeoutSeconds $ActionTimeoutSeconds
            $actionStartedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            $controlState.status = 'AwaitingVisualConfirmation'
            $controlState.updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            Write-AtomicJson -Path $controlStatePath -Value $controlState
            Wait-ForControlSignal -Process $runner -Path $confirmedSignalPath `
                -Description "the visual-confirmation signal for $($step.id)" `
                -TimeoutSeconds $ActionTimeoutSeconds
            $actionConfirmedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            $controlState.status = 'Holding'
            $controlState.updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            Write-AtomicJson -Path $controlStatePath -Value $controlState
        }
        else {
            [void](Read-Host 'Press Enter immediately before making this Synapse change')
            $actionStartedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            [void](Read-Host 'Make the change now, then press Enter immediately after it is visibly applied')
            $actionConfirmedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
        $action = [ordered]@{
            id = $step.id
            instruction = $step.instruction
            actionStartedAtUtc = $actionStartedAtUtc
            actionConfirmedAtUtc = $actionConfirmedAtUtc
            holdSeconds = $step.holdSeconds
            holdCompletedAtUtc = $null
            visuallyConfirmed = $true
        }
        [void]$annotations.actions.Add($action)
        Write-AtomicJson -Path $annotationPath -Value $annotations
        Wait-ForObservation -Seconds $step.holdSeconds -StepId $step.id
        $action.holdCompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Write-AtomicJson -Path $annotationPath -Value $annotations
        if ($FileControlled) {
            $controlState.status = 'HoldCompleted'
            $controlState.updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            Write-AtomicJson -Path $controlStatePath -Value $controlState
        }
    }
}
catch {
    $sequenceFailure = $_
}
finally {
    # Publish the stop request for every started runner, including failures or
    # cancellation before readiness. The runner observes it once ready and its
    # own finally block performs the reviewed targeted graceful shutdown.
    if ($runnerStarted -and -not (Test-Path -LiteralPath $stopPath -PathType Leaf)) {
        [IO.File]::WriteAllText($stopPath, 'stop', [Text.UTF8Encoding]::new($false))
    }
}

try {
    if (-not $runnerStarted) {
        throw $sequenceFailure
    }
    if (-not $runner.WaitForExit(30000)) {
        throw "The capture runner did not finish after the safe stop sentinel. Do not kill USBPcap; inspect $statePath and follow the owned-session graceful-shutdown instructions in captures/README.md."
    }
    $runnerOutput = $runnerOutputTask.GetAwaiter().GetResult()
    $runnerError = $runnerErrorTask.GetAwaiter().GetResult()
    if ($runner.ExitCode -ne 0) {
        throw "The capture runner failed with exit code $($runner.ExitCode).`n$runnerOutput`n$runnerError"
    }
    if ($null -ne $sequenceFailure) {
        throw "The interactive lighting sequence was interrupted after the capture runner started. The runner was asked to stop gracefully.`n$($sequenceFailure.Exception.Message)"
    }
}
finally {
    $runner.Dispose()
}

$finalState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($finalState.status -cne 'Completed' -or $finalState.stopMode -cne 'Graceful') {
    throw 'The capture did not finish through the reviewed graceful shutdown path.'
}
if (-not (Test-Path -LiteralPath $pcapPath -PathType Leaf) -or
    (Get-Item -LiteralPath $pcapPath).Length -eq 0) {
    throw 'The graceful capture did not produce a nonempty PCAP.'
}

$decodeOutput = & $resolvedTshark -n -r $pcapPath -c 1 -T fields -e frame.number 2>&1
if ($LASTEXITCODE -ne 0 -or @($decodeOutput | Where-Object { $_ -match '^\d+$' }).Count -eq 0) {
    throw "tshark could not decode a packet from the finalized PCAP: $($decodeOutput -join [Environment]::NewLine)"
}

$pcapItem = Get-Item -LiteralPath $pcapPath
$annotations.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$annotations.gracefulStopConfirmed = $true
$annotations.decodable = $true
$annotations.pcap = [ordered]@{
    fileName = $pcapItem.Name
    byteLength = $pcapItem.Length
    sha256 = (Get-FileHash -LiteralPath $pcapPath -Algorithm SHA256).Hash
    rawCaptureCommitted = $false
}
Write-AtomicJson -Path $annotationPath -Value $annotations

[pscustomobject]@{
    Status = 'Completed'
    PcapPath = $pcapPath
    AnnotationPath = $annotationPath
    StatePath = $statePath
    StopMode = 'Graceful'
    Decodable = $true
}
