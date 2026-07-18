[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workflow = Join-Path $repository 'captures\tools\Invoke-LightingSpectrumRandomCapture.ps1'
$runner = Join-Path $repository 'captures\tools\Invoke-InteractiveUsbPcapCapture.ps1'
$startHelper = Join-Path $repository 'captures\tools\Start-UsbPcapCapture.ps1'
$stopHelper = Join-Path $repository 'captures\tools\Stop-UsbPcapCapture.ps1'
$planPath = Join-Path $repository `
    'captures\plans\2026-07-18-lighting-spectrum-random-oracle.json'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if (($Value.Length -gt 0) -and ($Value -notmatch '[\s"]')) {
        return $Value
    }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

foreach ($path in @($workflow, $runner, $startHelper, $stopHelper, $planPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing capture workflow component: $path"
}

$workflowSource = Get-Content -LiteralPath $workflow -Raw
$runnerSource = Get-Content -LiteralPath $runner -Raw
$startSource = Get-Content -LiteralPath $startHelper -Raw
$stopSource = Get-Content -LiteralPath $stopHelper -Raw
$transitiveSource = @($workflowSource, $runnerSource, $startSource, $stopSource) -join "`n"

Assert-True ($workflowSource -notmatch '(?i)GenerateConsoleCtrlEvent|CTRL_(?:C|BREAK)_EVENT') `
    'The lighting workflow must not signal a console directly.'
Assert-True ($workflowSource -notmatch `
    '(?i)(?:Start|Stop)-UsbPcapCapture|GenerateConsoleCtrlEvent|CTRL_(?:C|BREAK)_EVENT|-ForceFallback') `
    'The lighting workflow must invoke only the reviewed runner and must not name a direct signal or helper path.'
Assert-True ($workflowSource -match "Invoke-InteractiveUsbPcapCapture\.ps1") `
    'The lighting workflow does not use the reviewed interactive runner.'
Assert-True ($runnerSource -notmatch '(?i)GenerateConsoleCtrlEvent|CTRL_C_EVENT|-ForceFallback') `
    'The interactive runner must neither signal a console nor enable forced shutdown.'
Assert-True ($runnerSource -match '(?i)processGroupId\s*-eq\s*0') `
    'The interactive runner must reject process-group zero before readiness.'
Assert-True ($startSource -match '(?i)CREATE_NEW_PROCESS_GROUP\s*=\s*0x0*200') `
    'The USBPcap launcher must create a new process group.'
Assert-True ($startSource -notmatch '(?i)CREATE_NEW_CONSOLE') `
    'The launcher must not combine CREATE_NEW_CONSOLE with CREATE_NEW_PROCESS_GROUP.'
Assert-True ($stopSource -match '(?i)CTRL_BREAK_EVENT\s*=\s*1') `
    'The stop helper must use CTRL_BREAK_EVENT.'
Assert-True ($stopSource -notmatch '(?i)CTRL_C_EVENT') `
    'No capture-path helper may name or use CTRL_C_EVENT.'
Assert-True ($stopSource -match '(?i)processGroupId\s*==\s*0') `
    'The signal worker must reject process-group zero.'
Assert-True ($stopSource -match `
    '(?i)GenerateConsoleCtrlEvent\s*\(\s*CTRL_BREAK_EVENT\s*,\s*processGroupId\s*\)') `
    'The signal worker must target only the recorded nonzero process group.'
Assert-True ($transitiveSource -notmatch `
    '(?i)GenerateConsoleCtrlEvent\s*\([^,\r\n]+,\s*(?:0|0x0+)[uUlL]*\s*\)') `
    'The reviewed lighting capture path must never signal process-group zero.'

$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
$steps = @($plan.steps)
$spectrumSteps = @($steps | Where-Object { $_.id -like 'spectrum-run-*' })
$randomSteps = @($steps | Where-Object { $_.id -eq 'breathing-random-long' })
$controlledSeconds = ($steps | Measure-Object -Property holdSeconds -Sum).Sum

Assert-True ($plan.schemaVersion -eq 1) 'The capture plan schema changed unexpectedly.'
Assert-True ($plan.id -ceq 'pid-02e0-lighting-spectrum-random-oracle-v1') `
    'The capture plan identity changed unexpectedly.'
Assert-True ($plan.target.modelNumber -ceq 'RZ09-0581' -and
    $plan.target.vendorIdHex -ceq '1532' -and
    $plan.target.productIdHex -ceq '02E0' -and
    $plan.target.bios -ceq '3.01') `
    'The capture plan is not bound to the exact target.'
Assert-True ($spectrumSteps.Count -eq 3) 'The plan must contain three independent Spectrum runs.'
Assert-True (@($spectrumSteps | Where-Object { $_.holdSeconds -ne 100 }).Count -eq 0) `
    'Every reviewed Spectrum run must remain 100 seconds long.'
Assert-True (-not ($plan.capture.PSObject.Properties.Name -contains 'spectrumExpectedCycleSeconds')) `
    'The oracle plan must not encode an unproven Spectrum cycle period.'
Assert-True ($randomSteps.Count -eq 1 -and $randomSteps[0].holdSeconds -eq 1800) `
    'The plan must retain one uninterrupted 1,800-second Random Breathing run.'
Assert-True ($controlledSeconds -eq 2160 -and
    $plan.capture.controlledObservationSeconds -eq 2160) `
    'The reviewed controlled-observation duration must remain 2,160 seconds.'
Assert-True ($plan.capture.timeoutSeconds -eq 2700 -and
    $plan.capture.timeoutSeconds -gt $controlledSeconds) `
    'The capture timeout must remain 2,700 seconds and leave time for manual transitions.'
Assert-True ($steps[-1].id -ceq 'restore-pre-capture-setting') `
    'The plan must end by restoring the recorded pre-capture setting.'
Assert-True ($workflowSource -match `
    '(?s)finally\s*\{.*?runnerStarted.*?WriteAllText\(\$stopPath') `
    'Every started runner must receive the stop sentinel, including failures before readiness.'
Assert-True ($workflowSource -match 'ReadToEndAsync\(\)') `
    'Redirected runner output must be drained asynchronously to prevent pipe deadlock.'
Assert-True ($workflowSource -match '(?i)CreateNoWindow\s*=\s*\$false') `
    'The runner must retain a console so its USBPcap child can receive targeted Ctrl+Break.'
Assert-True ($workflowSource -match '(?i)FileControlled') `
    'The workflow must support non-terminal control for Computer Use automation.'
Assert-True ($workflowSource -match '(?i)ApprovalPhrase\s*-cne\s*''CAPTURE SPECTRUM AND RANDOM BREATHING''') `
    'File-controlled capture must preserve the reviewed approval phrase gate.'
Assert-True ($workflowSource -match '(?i)AwaitingActionStart' -and
    $workflowSource -match '(?i)AwaitingVisualConfirmation') `
    'File-controlled capture must bracket every Synapse action with explicit signals.'
Assert-True ($workflowSource -match '(?i)Wait-ForControlSignal') `
    'File-controlled capture must wait on bounded control signals rather than terminal input.'
Assert-True ($workflowSource -match '(?i)FileControlDirectory') `
    'File-controlled capture must support a pre-created controller-owned signal directory.'
Assert-True ($workflowSource -match '(?i)::Replace\(\$temporaryPath,\s*\$Path,\s*\$backupPath') `
    'Atomic checkpoint updates must use a real backup path under Windows PowerShell.'
Assert-True (@($plan.annotationGuidance | Where-Object { $_ -match 'raw PCAP outside' }).Count -eq 1) `
    'The plan must forbid committing the raw capture.'
Assert-True (@($plan.annotationGuidance | Where-Object { $_ -match 'not OpenBlade typed-apply' }).Count -eq 1) `
    'The plan must preserve the Synapse-oracle evidence boundary.'

# Exercise the failure-before-readiness path with a fake runner. No USBPcap,
# console event, service, elevation, Synapse, or hardware is touched.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('OpenBlade.LightingCaptureWorkflow.Tests.' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $fakeRunner = Join-Path $testRoot 'Invoke-InteractiveUsbPcapCapture.ps1'
    $fakeUsbPcap = Join-Path $testRoot 'USBPcapCMD.exe'
    $fakeTshark = Join-Path $testRoot 'tshark.exe'
    $outputDirectory = Join-Path $testRoot 'capture-output'
    $runnerStartedPath = Join-Path $testRoot 'runner.started'
    $runnerStoppedPath = Join-Path $testRoot 'runner.stopped'
    [IO.File]::WriteAllBytes($fakeUsbPcap, [byte[]]@(0))
    [IO.File]::WriteAllBytes($fakeTshark, [byte[]]@(0))
    [IO.File]::WriteAllText($fakeRunner, @'
[CmdletBinding()]
param(
    [string]$UsbPcapDevice,
    [int]$DeviceAddress,
    [string]$OutputDirectory,
    [int]$TimeoutSeconds,
    [string]$UsbPcapExecutablePath
)
$root = Split-Path -Parent $OutputDirectory
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'runner.started'), 'started')
$stopPath = Join-Path $OutputDirectory 'capture.stop'
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
while (-not (Test-Path -LiteralPath $stopPath) -and [DateTimeOffset]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 25
}
if (-not (Test-Path -LiteralPath $stopPath)) {
    exit 9
}
[IO.File]::WriteAllText((Join-Path $root 'runner.stopped'), 'stopped')
exit 0
'@, [Text.UTF8Encoding]::new($false))

    $arguments = @(
        '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $workflow,
        '-UsbPcapDevice', '\\.\USBPcap3',
        '-DeviceAddress', '2',
        '-OutputDirectory', $outputDirectory,
        '-RunnerPath', $fakeRunner,
        '-PlanPath', $planPath,
        '-UsbPcapExecutablePath', $fakeUsbPcap,
        '-TsharkPath', $fakeTshark,
        '-ReadyTimeoutSeconds', '1')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell.exe'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = (($arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    Assert-True $process.Start() 'Could not start the fake lighting workflow process.'
    try {
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine('CAPTURE SPECTRUM AND RANDOM BREATHING')
        $process.StandardInput.WriteLine('Static white at 50%')
        $process.StandardInput.Close()
        Assert-True ($process.WaitForExit(10000)) `
            'The failure-before-readiness workflow did not finish within ten seconds.'
        $output = $standardOutput.GetAwaiter().GetResult()
        $errorOutput = $standardError.GetAwaiter().GetResult()
        Assert-True ($process.ExitCode -ne 0) `
            'The fake runner without a readiness marker unexpectedly succeeded.'
        Assert-True (Test-Path -LiteralPath $runnerStartedPath -PathType Leaf) `
            "The fake runner did not start: $output $errorOutput"
        Assert-True (Test-Path -LiteralPath $runnerStoppedPath -PathType Leaf) `
            "The fake runner did not observe the stop sentinel after readiness failed: $output $errorOutput"
        Assert-True (Test-Path -LiteralPath (Join-Path $outputDirectory 'capture.stop') -PathType Leaf) `
            'The workflow did not publish a stop sentinel after failure before readiness.'
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $process.Dispose()
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTestRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected test path $resolvedTestRoot."
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host 'Lighting Spectrum/Random capture-workflow tests passed.'
