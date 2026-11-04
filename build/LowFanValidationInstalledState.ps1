# Shared developer-only installed-state restoration after a completed low-fan probe.
# This file has no entry point, process actions, or direct hardware access.

function Get-LowFanIntent($Status, [string[]]$Exclude = @()) {
    $intent = [ordered]@{}
    foreach ($name in @('Profiles', 'DesiredState', 'PerformanceOverride', 'FanOverride',
            'FanControlOverride', 'LightingOverride', 'FullChargeOverride', 'BatteryPolicyOverride',
            'BatteryProtectionOverride', 'ActiveApplicationProfileId')) {
        if ($name -notin $Exclude) { $intent[$name] = $Status.$name }
    }
    ConvertTo-Json -InputObject $intent -Depth 25 -Compress
}

function ConvertTo-LowFanJson($Value) {
    ConvertTo-Json -InputObject $Value -Depth 25 -Compress
}

function Write-LowFanDurableRecord([string]$Path, $Value) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-LowFanJson $Value))
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
}

function Assert-LowFanKnownAutomatic($Status, $Baseline) {
    if ($Status.DeviceVerified -ne $true -or $Status.WritesBlocked -ne $false -or
        [string]$Status.ControlAccess -ne 'Active' -or $null -eq $Status.ConfirmedState -or
        $null -eq $Status.ConfirmedState.Performance -or
        [string]$Status.ConfirmedState.Fans -ne 'Automatic' -or
        $Status.ConfirmedState.HyperBoostActive -ne $false -or
        @($Status.UnconfirmedWrites).Count -ne 0 -or
        @($Status.PotentialControllers).Count -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($Status.ControllerDetectionWarning) -or
        $Status.ActivePowerSource -ne $Baseline.ActivePowerSource -or
        $Status.ActiveApplicationProfileId -ne $Baseline.ActiveApplicationProfileId) {
        throw 'Installed restoration requires fresh verified Active Automatic state, unchanged power/application context, and no controller or unconfirmed-write warning.'
    }
}

function Assert-LowFanOnlyPerformanceOverrideMissing($Status, $Baseline) {
    if ($null -ne $Status.PerformanceOverride -or $null -eq $Baseline.PerformanceOverride -or
        [string]$Baseline.PerformanceOverride.Lifetime -ne 'UntilPowerSourceChange') {
        throw 'Only the exact original temporary performance override may be restored when absent.'
    }
    if ((Get-LowFanIntent $Status -Exclude 'PerformanceOverride', 'DesiredState') -cne
        (Get-LowFanIntent $Baseline -Exclude 'PerformanceOverride', 'DesiredState')) {
        throw 'Other saved installed intent changed; no performance override was submitted.'
    }

    # Dropping the temporary override changes derived performance fields as well.
    # Check that exact expected change; do not ignore arbitrary DesiredState drift.
    # Only the observed Value/Source change is admitted; custom/GPU and all other desired fields must stay identical.
    $sourceName = [string]$Status.ActivePowerSource
    if ($sourceName -notin 'PluggedIn', 'Battery', 'UsbC') { throw 'The active power source is unknown.' }
    $profile = $Status.Profiles.$sourceName
    $application = $null
    if ($null -ne $Status.ActiveApplicationProfileId) {
        $matches = @($Status.Profiles.ApplicationProfiles | Where-Object {
            $_.Enabled -eq $true -and $_.Id -eq $Status.ActiveApplicationProfileId
        })
        if ($matches.Count -ne 1) { throw 'The active application profile is ambiguous.' }
        $application = $matches[0].Overrides.ForPowerSource($Status.ActivePowerSource)
    }
    $performance = if ($null -ne $application.Performance) { $application.Performance } else { $profile.Performance }
    $source = if ($null -ne $application.Performance -or $null -ne $application.CustomPerformance -or
        $null -ne $application.GpuClockTuning) { 'ApplicationProfile' } else { 'PowerProfile' }
    if ($Status.DesiredState.Performance.Value -ne $performance -or
        [string]$Status.DesiredState.Performance.Source -ne $source -or
        $Baseline.DesiredState.Performance.Value -ne $Baseline.PerformanceOverride.Value -or
        [string]$Baseline.DesiredState.Performance.Source -ne 'TemporaryOverride') {
        throw 'Derived performance intent does not match the missing original override.'
    }
    $baselineOther = [ordered]@{}
    $currentOther = [ordered]@{}
    foreach ($property in $Baseline.DesiredState.PSObject.Properties) {
        if ($property.Name -ne 'Performance') {
            $baselineOther[$property.Name] = $property.Value
        }
    }
    foreach ($property in $Status.DesiredState.PSObject.Properties) {
        if ($property.Name -ne 'Performance') {
            $currentOther[$property.Name] = $property.Value
        }
    }
    if ((ConvertTo-LowFanJson $baselineOther) -cne (ConvertTo-LowFanJson $currentOther)) {
        throw 'Other derived installed intent changed; no performance override was submitted.'
    }
}

function Restore-LowFanInstalledState($Client, $Baseline, $Handoff, [string]$RecordDirectory,
    $Sensors, [Threading.CancellationToken]$CancellationToken) {
    # GetStatus startup polling is only presentation readiness. This independent
    # observation supplies current service admission before deciding on any write.
    $current = $Client.ObserveStateAsync($Sensors, $CancellationToken).GetAwaiter().GetResult()
    Write-LowFanDurableRecord (Join-Path $RecordDirectory 'installed-override-admission.json') $current
    Assert-LowFanKnownAutomatic $current $Baseline
    if ((Get-LowFanIntent $current) -ceq (Get-LowFanIntent $Baseline)) {
        if ($current.ConfirmedState.Performance -ne $Baseline.ConfirmedState.Performance) {
            throw 'Installed intent matches but the original performance readback is not confirmed; no setting was replayed.'
        }
        return $current
    }
    # SafeToResume/no writes alone do not authorize this separate service mutation.
    if ($Handoff.Restored -ne $true -or $Handoff.IndeterminatePartialWrite -ne $false) {
        throw 'Confirmed probe restoration without ambiguity is required before restoring installed intent.'
    }
    Assert-LowFanOnlyPerformanceOverrideMissing $current $Baseline
    Write-LowFanDurableRecord (Join-Path $RecordDirectory 'installed-override-intent.json') ([ordered]@{
        Operation = 'SetPerformanceConfigurationOverrideAsync'
        Timestamp = [DateTimeOffset]::UtcNow
        OriginalPerformanceOverride = $Baseline.PerformanceOverride
        ObservedRevision = $current.Revision
        ActivePowerSource = $current.ActivePowerSource
        ActiveApplicationProfileId = $current.ActiveApplicationProfileId
    })
    try {
        # Pass the original typed object, including lifetime, custom and GPU fields.
        # No reconstructed ManualOverride, snapshot deserialization, or retry.
        $response = $Client.SetPerformanceConfigurationOverrideAsync($Baseline.PerformanceOverride, $CancellationToken).GetAwaiter().GetResult()
        Write-LowFanDurableRecord (Join-Path $RecordDirectory 'installed-override-response.json') $response
        if ([string]$response.Status -ne 'Applied') { throw 'Installed override restoration was not Applied; do not retry.' }
        $after = $Client.ObserveStateAsync($Sensors, $CancellationToken).GetAwaiter().GetResult()
        Write-LowFanDurableRecord (Join-Path $RecordDirectory 'installed-override-readback.json') $after
        Assert-LowFanKnownAutomatic $after $Baseline
        if ((Get-LowFanIntent $after) -cne (Get-LowFanIntent $Baseline) -or
            $after.ConfirmedState.Performance -ne $Baseline.ConfirmedState.Performance) {
            throw 'Installed restoration readback does not match all original intent; do not retry.'
        }
        return $after
    }
    catch {
        Write-LowFanDurableRecord (Join-Path $RecordDirectory 'installed-override-failure.json') ([ordered]@{
            Timestamp = [DateTimeOffset]::UtcNow; Error = $_.Exception.Message; MayHaveSubmitted = $true; RetryAllowed = $false
        })
        throw
    }
}
