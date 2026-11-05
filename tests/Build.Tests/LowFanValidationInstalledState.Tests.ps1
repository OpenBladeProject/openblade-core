$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repoRoot 'build/LowFanValidationInstalledState.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade-LowFanInstalledState-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$script:checks = 0
function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Copy-TestValue($Value) { ConvertFrom-Json (ConvertTo-LowFanJson $Value) }
function New-Case([string]$Performance = 'Silent', [string]$Power = 'PluggedIn') {
    $profile = [pscustomobject]@{ Performance = if ($Performance -eq 'Silent') { 'Balanced' } else { 'BatteryBalanced' } }
    $baseline = [pscustomobject]@{
        Revision = 10; DeviceVerified = $true; WritesBlocked = $false; ControlAccess = 'Active'
        ConfirmedState = [pscustomobject]@{ Performance = $Performance; Fans = 'Automatic'; HyperBoostActive = $false }
        UnconfirmedWrites = @(); PotentialControllers = @(); ControllerDetectionWarning = $null
        ActivePowerSource = $Power; ActiveApplicationProfileId = $null
        Profiles = [pscustomobject]@{ PluggedIn = $profile; Battery = $profile; UsbC = $profile; ApplicationProfiles = @() }
        DesiredState = [pscustomobject]@{
            Performance = [pscustomobject]@{ Value = $Performance; Source = 'TemporaryOverride' }
            Fans = [pscustomobject]@{ Value = 'Automatic'; Source = 'PowerProfile' }
            CustomPerformance = $null; GpuClockTuning = $null; FanControl = 'saved-automatic'; Lighting = 'saved-lighting'; Battery = 'saved-battery'
        }
        PerformanceOverride = [pscustomobject]@{ Value = $Performance; Lifetime = 'UntilPowerSourceChange'; CustomPerformance = $null; GpuClockTuning = $null }
        FanOverride = $null; FanControlOverride = $null; LightingOverride = $null; FullChargeOverride = $false
        BatteryPolicyOverride = $null; BatteryProtectionOverride = $null
    }
    $current = Copy-TestValue $baseline
    $current.PerformanceOverride = $null
    $current.DesiredState.Performance = [pscustomobject]@{ Value = $profile.Performance; Source = 'PowerProfile' }
    $current.ConfirmedState.Performance = $profile.Performance
    $path = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path
    $client = [pscustomobject]@{
        Current = $current; After = $baseline; Path = $path; ObserveCalls = 0; Writes = 0; Submitted = $null
        Response = [pscustomobject]@{ Status = 'Applied'; Message = 'fake applied' }; ThrowSetter = $false; ThrowObserve = $false
    }
    $client | Add-Member ScriptMethod ObserveStateAsync {
        param($Sensors, $Token)
        $this.ObserveCalls++
        if ($this.ThrowObserve) { throw 'fake observation failure' }
        $value = if ($this.ObserveCalls -eq 1) { $this.Current } else { $this.After }
        [Threading.Tasks.Task[object]]::FromResult($value)
    }
    $client | Add-Member ScriptMethod SetPerformanceConfigurationOverrideAsync {
        param($Value, $Token)
        $this.Writes++
        $this.Submitted = $Value
        if (-not (Test-Path -LiteralPath (Join-Path $this.Path 'installed-override-intent.json'))) {
            throw 'IPC occurred before the durable intent existed.'
        }
        if ($this.ThrowSetter) { throw 'fake ambiguous setter failure' }
        [Threading.Tasks.Task[object]]::FromResult($this.Response)
    }
    [pscustomobject]@{ Client = $client; Baseline = $baseline; Handoff = [pscustomobject]@{ Restored = $true; IndeterminatePartialWrite = $false; SafeToResume = $true }; Path = $path }
}
function Invoke-Case($Case) {
    Restore-LowFanInstalledState $Case.Client $Case.Baseline $Case.Handoff $Case.Path 'fake-all' ([Threading.CancellationToken]::None)
}
function Assert-Rejected($Case, [int]$Writes = 0) {
    $failed = $false
    try { $null = Invoke-Case $Case } catch { $failed = $true }
    Assert-True $failed 'An unsafe or ambiguous restoration was accepted.'
    Assert-True ($Case.Client.Writes -eq $Writes) 'Unexpected setter count or replay.'
}
try {
    foreach ($pair in @(@('Silent', 'PluggedIn'), @('BatterySaver', 'Battery'))) {
        $case = New-Case $pair[0] $pair[1]
        $after = Invoke-Case $case
        Assert-True ($case.Client.Writes -eq 1 -and $case.Client.ObserveCalls -eq 2) 'Expected one submission and independent readback.'
        Assert-True ([object]::ReferenceEquals($case.Client.Submitted, $case.Baseline.PerformanceOverride)) 'Original typed override object was not preserved.'
        Assert-True ((Get-LowFanIntent $after) -ceq (Get-LowFanIntent $case.Baseline)) 'All ten intent fields were not restored.'
        Assert-True (@((Get-LowFanIntent $after | ConvertFrom-Json).PSObject.Properties).Count -eq 10) 'Intent comparison is missing fields.'
        foreach ($file in @('installed-override-intent.json', 'installed-override-response.json', 'installed-override-readback.json')) {
            Assert-True (Test-Path -LiteralPath (Join-Path $case.Path $file)) "Missing separate record: $file"
        }
    }
    $case = New-Case
    $case.Client.Current = $case.Baseline
    $null = Invoke-Case $case
    Assert-True ($case.Client.Writes -eq 0) 'Identical installed intent was replayed.'
    $case = New-Case
    $gpu = [pscustomobject]@{ Enabled = $true; Offsets = [pscustomobject]@{ CoreMegahertz = 25; MemoryMegahertz = 50 } }
    $case.Baseline.PerformanceOverride.GpuClockTuning = $gpu
    $case.Baseline.DesiredState.GpuClockTuning = $gpu
    $case.Client.Current.DesiredState.GpuClockTuning = $gpu
    $null = Invoke-Case $case
    Assert-True ([object]::ReferenceEquals($case.Client.Submitted.GpuClockTuning, $gpu)) 'Original optional GPU settings were dropped or reconstructed.'
    foreach ($field in @('Profiles', 'FanOverride', 'FanControlOverride', 'LightingOverride', 'FullChargeOverride',
            'BatteryPolicyOverride', 'BatteryProtectionOverride', 'ActiveApplicationProfileId')) {
        $case = New-Case
        $case.Client.Current.$field = 'drift'
        Assert-Rejected $case
    }
    foreach ($field in @('Fans', 'Lighting', 'Battery', 'CustomPerformance', 'GpuClockTuning', 'FanControl')) {
        $case = New-Case
        $case.Client.Current.DesiredState.$field = 'drift'
        Assert-Rejected $case
    }
    $mutations = @(
        { param($c) $c.Client.Current.PerformanceOverride = [pscustomobject]@{ Value = 'Performance' } },
        { param($c) $c.Baseline.PerformanceOverride.Lifetime = 'Persistent' },
        { param($c) $c.Handoff.Restored = $false },
        { param($c) $c.Handoff.IndeterminatePartialWrite = $true },
        { param($c) $c.Handoff.IndeterminatePartialWrite = $null },
        { param($c) $c.Client.Current.ActivePowerSource = 'Battery' },
        { param($c) $c.Client.Current.DeviceVerified = $false },
        { param($c) $c.Client.Current.WritesBlocked = $true },
        { param($c) $c.Client.Current.ControlAccess = 'Unavailable' },
        { param($c) $c.Client.Current.UnconfirmedWrites = @('Performance') },
        { param($c) $c.Client.Current.PotentialControllers = @('Synapse') },
        { param($c) $c.Client.Current.ControllerDetectionWarning = 'unknown' },
        { param($c) $c.Client.Current.ConfirmedState.Fans = 'Maximum' },
        { param($c) $c.Client.Current.ConfirmedState.HyperBoostActive = $true },
        { param($c) $c.Client.Current.ConfirmedState.Performance = $null },
        { param($c) $c.Client.Current.DesiredState.Performance.Value = 'Performance' },
        { param($c) $c.Client.Current.DesiredState.Performance.Source = 'ConfirmedFirmwareState' },
        { param($c) $c.Client.ThrowObserve = $true }
    )
    foreach ($mutation in $mutations) {
        $case = New-Case
        & $mutation $case
        Assert-Rejected $case
    }
    foreach ($failure in @('ThrowSetter', 'Blocked', 'ReadbackDrift', 'ReadbackUnknown')) {
        $case = New-Case
        switch ($failure) {
            'ThrowSetter' { $case.Client.ThrowSetter = $true }
            'Blocked' { $case.Client.Response.Status = 'Blocked' }
            'ReadbackDrift' { $case.Client.After = Copy-TestValue $case.Baseline; $case.Client.After.LightingOverride = 'drift' }
            'ReadbackUnknown' { $case.Client.After = Copy-TestValue $case.Baseline; $case.Client.After.ConfirmedState.Performance = $null }
        }
        Assert-Rejected $case 1
        Assert-True (Test-Path -LiteralPath (Join-Path $case.Path 'installed-override-failure.json')) 'Ambiguous attempt failure was not retained.'
    }
    $case = New-Case
    'existing intent' | Set-Content -LiteralPath (Join-Path $case.Path 'installed-override-intent.json')
    Assert-Rejected $case
    foreach ($path in @('build/Invoke-SilentLowFanValidation.ps1', 'build/Invoke-BatterySaverLowFanValidation.ps1', 'build/LowFanValidationInstalledState.ps1')) {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot $path), [ref]$tokens, [ref]$errors)
        Assert-True ($errors.Count -eq 0) "Parse failure in $path"
    }
    $rejected = $false
    try {
        & (Join-Path $repoRoot 'build/Invoke-SilentLowFanValidation.ps1') -CapturePath (Join-Path $testRoot 'never-run.exe') `
            -RecordDirectory (Join-Path $testRoot 'never-created') -Calibrate -LiteralZero
    }
    catch {
        $rejected = $_.Exception.Message -eq 'LiteralZero and Calibrate are separate one-shot scenarios.'
    }
    Assert-True $rejected 'Combined calibration and literal-zero selection must fail before any owner or service action.'
    Write-Host "Low-fan installed-state tests passed ($script:checks assertions). No service, IPC, or hardware actions ran."
}
finally {
    # Delete only this harness's fresh temporary directory, in one PowerShell scope.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'OpenBlade-LowFanInstalledState-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
