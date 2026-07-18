[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CapturePath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AnnotationPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateNotNullOrEmpty()]
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe',

    [ValidateNotNullOrEmpty()]
    [string]$PlanPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ReviewedPlanSha256 =
    '9E69E5BC5E03B4F4323C39DF3F918DAF5895FF02CF3AF58CB5E1383ECF868DDD'
$CapturedHoleSlots = [int[]]@(
    0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93)

function ConvertFrom-HexString {
    param([Parameter(Mandatory)][string]$Value)

    if (($Value.Length % 2) -ne 0) {
        throw 'A capture field contained an odd-length hexadecimal value.'
    }

    $bytes = [byte[]]::new($Value.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($Value.Substring($index * 2, 2), 16)
    }
    return ,$bytes
}

function Get-Median {
    param([Parameter(Mandatory)][double[]]$Values)

    if ($Values.Count -eq 0) {
        return $null
    }
    [double[]]$sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Get-Statistics {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][double[]]$Values,
        [int]$Decimals = 3
    )

    if ($Values.Count -eq 0) {
        return [ordered]@{
            count = 0
            minimum = $null
            median = $null
            mean = $null
            maximum = $null
        }
    }

    $measure = $Values | Measure-Object -Minimum -Maximum -Average
    return [ordered]@{
        count = $Values.Count
        minimum = [Math]::Round([double]$measure.Minimum, $Decimals)
        median = [Math]::Round([double](Get-Median $Values), $Decimals)
        mean = [Math]::Round([double]$measure.Average, $Decimals)
        maximum = [Math]::Round([double]$measure.Maximum, $Decimals)
    }
}

function ConvertTo-HsvSummary {
    param(
        [Parameter(Mandatory)][byte]$Red,
        [Parameter(Mandatory)][byte]$Green,
        [Parameter(Mandatory)][byte]$Blue
    )

    $redUnit = $Red / 255.0
    $greenUnit = $Green / 255.0
    $blueUnit = $Blue / 255.0
    $maximum = [Math]::Max($redUnit, [Math]::Max($greenUnit, $blueUnit))
    $minimum = [Math]::Min($redUnit, [Math]::Min($greenUnit, $blueUnit))
    $delta = $maximum - $minimum

    $hue = $null
    if ($delta -gt 0) {
        if ($maximum -eq $redUnit) {
            $hue = 60.0 * ((($greenUnit - $blueUnit) / $delta) % 6.0)
        }
        elseif ($maximum -eq $greenUnit) {
            $hue = 60.0 * ((($blueUnit - $redUnit) / $delta) + 2.0)
        }
        else {
            $hue = 60.0 * ((($redUnit - $greenUnit) / $delta) + 4.0)
        }
        if ($hue -lt 0) {
            $hue += 360.0
        }
    }

    $saturation = if ($maximum -eq 0) { 0.0 } else { $delta / $maximum }
    return [pscustomobject]@{
        Hue = $hue
        Saturation = $saturation
        Value = $maximum
    }
}

function ConvertTo-MatrixSample {
    param([Parameter(Mandatory)][string]$Row)

    $fields = $Row -split "`t"
    if ($fields.Count -ne 3) {
        throw "Expected frame, epoch, and data fields; received: $Row"
    }

    [byte[]]$report = ConvertFrom-HexString $fields[2]
    if ($report.Length -ne 374 -or
        $report[0] -ne 0x00 -or
        $report[2] -ne 0x00 -or
        $report[3] -ne 0x00 -or
        $report[4] -ne 0x01 -or
        $report[5] -ne 0x36 -or
        $report[6] -ne 0x03 -or
        $report[7] -ne 0x0B -or
        $report[8] -ne 0x00 -or
        $report[9] -ne 0x00 -or
        $report[10] -ne 0x00 -or
        $report[11] -ne 0x65 -or
        $report[373] -ne 0x00) {
        throw "Frame $($fields[0]) is not the expected PID 02E0 102-slot matrix report."
    }

    for ($offset = 318; $offset -le 371; $offset++) {
        if ($report[$offset] -ne 0) {
            throw "Frame $($fields[0]) has nonzero PID 02E0 matrix padding."
        }
    }
    $checksum = [byte]0
    for ($offset = 2; $offset -lt 372; $offset++) {
        $checksum = $checksum -bxor $report[$offset]
    }
    if ($report[372] -ne $checksum) {
        throw "Frame $($fields[0]) has an invalid PID 02E0 matrix checksum."
    }

    $activeColors = [ordered]@{}
    for ($slot = 0; $slot -lt 102; $slot++) {
        $offset = 12 + ($slot * 3)
        $color = '{0:X2}{1:X2}{2:X2}' -f `
            $report[$offset], $report[$offset + 1], $report[$offset + 2]
        if ($CapturedHoleSlots -contains $slot) {
            if ($color -cne '000000') {
                throw "Frame $($fields[0]) lights captured layout-hole slot $slot."
            }
        }
        elseif (-not $activeColors.Contains($color)) {
            $activeColors[$color] = $true
        }
    }

    if ($activeColors.Count -ne 1) {
        throw "Frame $($fields[0]) is not uniform across the captured 86-key matrix mask."
    }

    $rgbHex = [string]@($activeColors.Keys)[0]
    $red = [Convert]::ToByte($rgbHex.Substring(0, 2), 16)
    $green = [Convert]::ToByte($rgbHex.Substring(2, 2), 16)
    $blue = [Convert]::ToByte($rgbHex.Substring(4, 2), 16)
    $hsv = ConvertTo-HsvSummary -Red $red -Green $green -Blue $blue

    return [pscustomobject]@{
        Frame = [int]$fields[0]
        Epoch = [double]::Parse($fields[1], [Globalization.CultureInfo]::InvariantCulture)
        RgbHex = $rgbHex
        Red = $red
        Green = $green
        Blue = $blue
        Hue = $hsv.Hue
        Saturation = $hsv.Saturation
        Value = $hsv.Value
        PeakChannel = [Math]::Max($red, [Math]::Max($green, $blue))
        IsBlack = $rgbHex -ceq '000000'
        LitSlotCount = if ($rgbHex -ceq '000000') { 0 } else { 86 }
    }
}

function ConvertTo-EpochSeconds {
    param([Parameter(Mandatory)][string]$Timestamp)

    return [DateTimeOffset]::Parse(
        $Timestamp,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUnixTimeMilliseconds() / 1000.0
}

function Get-ActionWindow {
    param(
        [Parameter(Mandatory)][object[]]$Actions,
        [Parameter(Mandatory)][string]$Id
    )

    $matches = @($Actions | Where-Object { $_.id -ceq $Id })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one completed annotation action named $Id."
    }
    $action = $matches[0]
    if (-not $action.visuallyConfirmed -or
        [string]::IsNullOrWhiteSpace([string]$action.actionConfirmedAtUtc) -or
        [string]::IsNullOrWhiteSpace([string]$action.holdCompletedAtUtc)) {
        throw "Annotation action $Id is not visually confirmed and complete."
    }

    $start = ConvertTo-EpochSeconds ([string]$action.actionConfirmedAtUtc)
    $end = ConvertTo-EpochSeconds ([string]$action.holdCompletedAtUtc)
    if ($end -le $start) {
        throw "Annotation action $Id has an invalid observation window."
    }
    return [pscustomobject]@{ Id = $Id; Start = $start; End = $end }
}

function Assert-ReviewedActionSequence {
    param(
        [Parameter(Mandatory)][object]$Plan,
        [Parameter(Mandatory)][object]$Annotation
    )

    [string[]]$expectedIds = @(
        'static-baseline',
        'spectrum-run-1',
        'static-separator-1',
        'spectrum-run-2',
        'static-separator-2',
        'spectrum-run-3',
        'static-separator-3',
        'breathing-random-long',
        'static-evidence-end',
        'restore-pre-capture-setting')
    [int[]]$expectedHolds = @(10, 100, 10, 100, 10, 100, 10, 1800, 10, 10)
    $planSteps = @($Plan.steps)
    $actions = @($Annotation.actions)
    if ($planSteps.Count -ne $expectedIds.Count -or
        $actions.Count -ne $expectedIds.Count) {
        throw 'The plan and annotation must contain the complete reviewed ten-step action sequence.'
    }

    $captureStarted = ConvertTo-EpochSeconds ([string]$Annotation.startedAtUtc)
    $captureCompleted = ConvertTo-EpochSeconds ([string]$Annotation.completedAtUtc)
    [double]$previousCompleted = $captureStarted
    for ($index = 0; $index -lt $expectedIds.Count; $index++) {
        $step = $planSteps[$index]
        $action = $actions[$index]
        if ([string]$step.id -cne $expectedIds[$index] -or
            [int]$step.holdSeconds -ne $expectedHolds[$index] -or
            [string]$action.id -cne [string]$step.id -or
            [string]$action.instruction -cne [string]$step.instruction -or
            [int]$action.holdSeconds -ne [int]$step.holdSeconds -or
            -not $action.visuallyConfirmed) {
            throw "Annotation action $index does not match the complete reviewed plan sequence."
        }

        $started = ConvertTo-EpochSeconds ([string]$action.actionStartedAtUtc)
        $confirmed = ConvertTo-EpochSeconds ([string]$action.actionConfirmedAtUtc)
        $completed = ConvertTo-EpochSeconds ([string]$action.holdCompletedAtUtc)
        if ($started -lt $previousCompleted -or
            $confirmed -lt $started -or
            $completed -le $confirmed -or
            ($completed - $confirmed) -lt ($expectedHolds[$index] - 0.01) -or
            ($completed - $confirmed) -gt ($expectedHolds[$index] + 2.0)) {
            throw "Annotation action $($action.id) has overlapping, non-monotonic, or unbounded timestamps."
        }
        $previousCompleted = $completed
    }

    if ($captureCompleted -lt $previousCompleted) {
        throw 'The capture completion timestamp precedes the reviewed restoration step.'
    }
}

function Get-WindowSamples {
    param(
        [Parameter(Mandatory)][object[]]$Samples,
        [Parameter(Mandatory)][object]$Window
    )

    return @($Samples | Where-Object {
        $_.Epoch -ge $Window.Start -and $_.Epoch -le $Window.End
    })
}

function Get-SpectrumRunEvidence {
    param(
        [Parameter(Mandatory)][object]$Window,
        [Parameter(Mandatory)][object[]]$Samples
    )

    $runSamples = @(Get-WindowSamples -Samples $Samples -Window $Window)
    if ($runSamples.Count -lt 3) {
        throw "Spectrum action $($Window.Id) contains fewer than three matrix samples."
    }
    if (@($runSamples | Where-Object { $_.IsBlack -or $null -eq $_.Hue }).Count -ne 0) {
        throw "Spectrum action $($Window.Id) contains an achromatic matrix sample."
    }

    [Collections.Generic.List[object]]$curve = [Collections.Generic.List[object]]::new()
    [Collections.Generic.List[double]]$cadence = [Collections.Generic.List[double]]::new()
    [double]$unwrappedHue = [double]$runSamples[0].Hue
    [double]$previousRawHue = [double]$runSamples[0].Hue
    [double]$previousEpoch = [double]$runSamples[0].Epoch
    [void]$curve.Add([ordered]@{
        elapsedMilliseconds = [Math]::Round(($runSamples[0].Epoch - $Window.Start) * 1000.0, 3)
        rgbHex = $runSamples[0].RgbHex
        hueDegrees = [Math]::Round([double]$runSamples[0].Hue, 6)
        saturation = [Math]::Round([double]$runSamples[0].Saturation, 6)
        value = [Math]::Round([double]$runSamples[0].Value, 6)
        unwrappedHueDegrees = [Math]::Round($unwrappedHue, 6)
    })

    for ($index = 1; $index -lt $runSamples.Count; $index++) {
        $sample = $runSamples[$index]
        $delta = [double]$sample.Hue - $previousRawHue
        while ($delta -le -180.0) { $delta += 360.0 }
        while ($delta -gt 180.0) { $delta -= 360.0 }
        $unwrappedHue += $delta
        [void]$cadence.Add(($sample.Epoch - $previousEpoch) * 1000.0)
        [void]$curve.Add([ordered]@{
            elapsedMilliseconds = [Math]::Round(($sample.Epoch - $Window.Start) * 1000.0, 3)
            rgbHex = $sample.RgbHex
            hueDegrees = [Math]::Round([double]$sample.Hue, 6)
            saturation = [Math]::Round([double]$sample.Saturation, 6)
            value = [Math]::Round([double]$sample.Value, 6)
            unwrappedHueDegrees = [Math]::Round($unwrappedHue, 6)
        })
        $previousRawHue = [double]$sample.Hue
        $previousEpoch = [double]$sample.Epoch
    }

    $netHueChange = [double]$curve[-1].unwrappedHueDegrees - [double]$curve[0].unwrappedHueDegrees
    $direction = if ($netHueChange -gt 0) { 'IncreasingHue' } elseif ($netHueChange -lt 0) { 'DecreasingHue' } else { 'Undetermined' }
    [Collections.Generic.List[object]]$crossings = [Collections.Generic.List[object]]::new()
    $oppositeDirectionSteps = 0
    if ($direction -cne 'Undetermined') {
        for ($index = 1; $index -lt $curve.Count; $index++) {
            $fromHue = [double]$curve[$index - 1].unwrappedHueDegrees
            $toHue = [double]$curve[$index].unwrappedHueDegrees
            $fromTime = [double]$curve[$index - 1].elapsedMilliseconds
            $toTime = [double]$curve[$index].elapsedMilliseconds
            if (($direction -ceq 'IncreasingHue' -and $toHue -lt $fromHue) -or
                ($direction -ceq 'DecreasingHue' -and $toHue -gt $fromHue)) {
                $oppositeDirectionSteps++
            }
            if ($toHue -eq $fromHue) {
                continue
            }

            [double[]]$boundaries = if ($direction -ceq 'IncreasingHue') {
                $firstTurn = [int][Math]::Floor($fromHue / 360.0) + 1
                $lastTurn = [int][Math]::Floor($toHue / 360.0)
                if ($lastTurn -ge $firstTurn) {
                    @($firstTurn..$lastTurn | ForEach-Object { $_ * 360.0 })
                }
                else { @() }
            }
            else {
                $firstTurn = [int][Math]::Ceiling($fromHue / 360.0) - 1
                $lastTurn = [int][Math]::Ceiling($toHue / 360.0)
                if ($firstTurn -ge $lastTurn) {
                    @($firstTurn..$lastTurn | ForEach-Object { $_ * 360.0 })
                }
                else { @() }
            }

            foreach ($boundary in $boundaries) {
                $fraction = ($boundary - $fromHue) / ($toHue - $fromHue)
                [void]$crossings.Add([ordered]@{
                    elapsedMilliseconds = [Math]::Round($fromTime + (($toTime - $fromTime) * $fraction), 3)
                    unwrappedHueBoundaryDegrees = [Math]::Round($boundary, 3)
                    turnIndex = [int][Math]::Round($boundary / 360.0)
                })
            }
        }
    }

    [Collections.Generic.List[double]]$periods =
        [Collections.Generic.List[double]]::new()
    $ambiguousCrossingTransitionCount = 0
    if ($crossings.Count -ge 2) {
        $expectedTurnDelta = if ($direction -ceq 'IncreasingHue') { 1 } else { -1 }
        for ($index = 1; $index -lt $crossings.Count; $index++) {
            $turnDelta = [int]$crossings[$index].turnIndex -
                [int]$crossings[$index - 1].turnIndex
            if ($turnDelta -eq $expectedTurnDelta) {
                [void]$periods.Add(
                [double]$crossings[$index].elapsedMilliseconds - [double]$crossings[$index - 1].elapsedMilliseconds
                )
            }
            else {
                $ambiguousCrossingTransitionCount++
            }
        }
    }

    return [ordered]@{
        actionId = $Window.Id
        sampleCount = $runSamples.Count
        firstSampleDelayMilliseconds = [Math]::Round(($runSamples[0].Epoch - $Window.Start) * 1000.0, 3)
        lastSampleElapsedMilliseconds = [Math]::Round(($runSamples[-1].Epoch - $Window.Start) * 1000.0, 3)
        cadenceMilliseconds = Get-Statistics -Values ([double[]]$cadence.ToArray())
        hueDirection = $direction
        netUnwrappedHueDegrees = [Math]::Round($netHueChange, 6)
        oppositeDirectionStepCount = $oppositeDirectionSteps
        wrapCrossings = $crossings
        ambiguousCrossingTransitionCount = $ambiguousCrossingTransitionCount
        completeCycleCount = $periods.Count
        completeCyclePeriodsMilliseconds = $periods.ToArray()
        completeCyclePeriodStatisticsMilliseconds = Get-Statistics `
            -Values ([double[]]$periods.ToArray())
        curveSamples = $curve
    }
}

function Get-RandomBreathingEvidence {
    param(
        [Parameter(Mandatory)][object]$Window,
        [Parameter(Mandatory)][object[]]$Samples
    )

    $windowSamples = @(Get-WindowSamples -Samples $Samples -Window $Window)
    if ($windowSamples.Count -lt 3) {
        throw 'The Random Breathing action contains fewer than three matrix samples.'
    }

    [Collections.Generic.List[object]]$segments = [Collections.Generic.List[object]]::new()
    [Collections.Generic.List[object]]$current = $null
    $blackObservedBefore = $false
    foreach ($sample in $windowSamples) {
        if ($sample.IsBlack) {
            if ($null -ne $current) {
                [void]$segments.Add([pscustomobject]@{
                    Samples = $current.ToArray()
                    BlackBefore = $blackObservedBefore
                    BlackAfter = $true
                })
                $current = $null
            }
            $blackObservedBefore = $true
            continue
        }
        if ($null -eq $current) {
            $current = [Collections.Generic.List[object]]::new()
        }
        [void]$current.Add($sample)
    }
    if ($null -ne $current) {
        [void]$segments.Add([pscustomobject]@{
            Samples = $current.ToArray()
            BlackBefore = $blackObservedBefore
            BlackAfter = $false
        })
    }

    [Collections.Generic.List[object]]$cycles = [Collections.Generic.List[object]]::new()
    foreach ($segment in $segments) {
        if (-not $segment.BlackBefore -or -not $segment.BlackAfter) {
            continue
        }
        [object[]]$cycleSamples = $segment.Samples
        $peak = ($cycleSamples | Measure-Object -Property PeakChannel -Maximum).Maximum
        $peakSamples = @($cycleSamples | Where-Object { $_.PeakChannel -eq $peak })
        $peakColors = @($peakSamples | ForEach-Object { $_.RgbHex } | Sort-Object -Unique)
        if ($peakColors.Count -ne 1) {
            throw 'A bounded Random Breathing cycle has multiple colors at its peak and cannot be summarized deterministically.'
        }
        $lastPeakIndex = [Array]::LastIndexOf($cycleSamples, $peakSamples[-1])
        $nextLower = if ($lastPeakIndex -lt ($cycleSamples.Count - 1)) {
            $cycleSamples[$lastPeakIndex + 1]
        }
        else { $null }

        [void]$cycles.Add([ordered]@{
            sequence = $cycles.Count + 1
            plateauRgbHex = $peakColors[0]
            peakChannelValue = [int]$peak
            firstNonBlackElapsedMilliseconds = [Math]::Round(($cycleSamples[0].Epoch - $Window.Start) * 1000.0, 3)
            firstPeakElapsedMilliseconds = [Math]::Round(($peakSamples[0].Epoch - $Window.Start) * 1000.0, 3)
            lastPeakReportElapsedMilliseconds = [Math]::Round(($peakSamples[-1].Epoch - $Window.Start) * 1000.0, 3)
            lastNonBlackElapsedMilliseconds = [Math]::Round(($cycleSamples[-1].Epoch - $Window.Start) * 1000.0, 3)
            peakReportCount = $peakSamples.Count
            observedPeakReportSpanMilliseconds = [Math]::Round(($peakSamples[-1].Epoch - $peakSamples[0].Epoch) * 1000.0, 3)
            nextLowerReportElapsedMilliseconds = if ($null -eq $nextLower) {
                $null
            }
            else {
                [Math]::Round(($nextLower.Epoch - $Window.Start) * 1000.0, 3)
            }
        })
    }

    [double[]]$peakIntervals = @()
    if ($cycles.Count -ge 2) {
        $peakIntervals = @(
            for ($index = 1; $index -lt $cycles.Count; $index++) {
                [double]$cycles[$index].firstPeakElapsedMilliseconds -
                    [double]$cycles[$index - 1].firstPeakElapsedMilliseconds
            })
    }

    $colorCounts = [ordered]@{}
    $transitionCounts = [ordered]@{}
    $consecutiveRepeatCount = 0
    for ($index = 0; $index -lt $cycles.Count; $index++) {
        $color = [string]$cycles[$index].plateauRgbHex
        if ($colorCounts.Contains($color)) { $colorCounts[$color]++ } else { $colorCounts[$color] = 1 }
        if ($index -gt 0) {
            $previous = [string]$cycles[$index - 1].plateauRgbHex
            if ($previous -ceq $color) { $consecutiveRepeatCount++ }
            $transition = "$previous->$color"
            if ($transitionCounts.Contains($transition)) { $transitionCounts[$transition]++ } else { $transitionCounts[$transition] = 1 }
        }
    }

    $colorFrequency = @($colorCounts.GetEnumerator() | ForEach-Object {
        [ordered]@{ rgbHex = [string]$_.Key; count = [int]$_.Value }
    })
    $transitions = @($transitionCounts.GetEnumerator() | ForEach-Object {
        $parts = [string]$_.Key -split '->'
        [ordered]@{ fromRgbHex = $parts[0]; toRgbHex = $parts[1]; count = [int]$_.Value }
    })

    return [ordered]@{
        actionId = $Window.Id
        sampleCount = $windowSamples.Count
        nonBlackSegmentCount = $segments.Count
        boundedCycleCount = $cycles.Count
        excludedPartialSegmentCount = $segments.Count - $cycles.Count
        plateauSequenceRgbHex = @($cycles | ForEach-Object { $_.plateauRgbHex })
        distinctPlateauColorCount = $colorCounts.Count
        plateauColorFrequency = $colorFrequency
        consecutiveRepeatedPlateauColorCount = $consecutiveRepeatCount
        transitions = $transitions
        peakToPeakIntervalsMilliseconds = $peakIntervals
        peakToPeakStatisticsMilliseconds = Get-Statistics -Values $peakIntervals
        cycles = $cycles
    }
}

if ([string]::IsNullOrWhiteSpace($PlanPath)) {
    $PlanPath = Join-Path (Split-Path -Parent $PSScriptRoot) `
        'plans\2026-07-18-lighting-spectrum-random-oracle.json'
}

$resolvedCapture = (Resolve-Path -LiteralPath $CapturePath -ErrorAction Stop).Path
$resolvedAnnotation = (Resolve-Path -LiteralPath $AnnotationPath -ErrorAction Stop).Path
$resolvedPlan = (Resolve-Path -LiteralPath $PlanPath -ErrorAction Stop).Path
$resolvedTshark = (Resolve-Path -LiteralPath $TsharkPath -ErrorAction Stop).Path
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Refusing to overwrite existing analysis evidence: $resolvedOutput"
}

$annotation = Get-Content -LiteralPath $resolvedAnnotation -Raw | ConvertFrom-Json
$plan = Get-Content -LiteralPath $resolvedPlan -Raw | ConvertFrom-Json
if ($annotation.schemaVersion -ne 1 -or
    $annotation.planId -cne 'pid-02e0-lighting-spectrum-random-oracle-v1' -or
    $plan.schemaVersion -ne 1 -or
    $plan.id -cne $annotation.planId) {
    throw 'The annotation or capture plan has an unsupported identity or schema.'
}
if ($plan.target.model -cne 'Razer Blade 16 (2026)' -or
    $plan.target.modelNumber -cne 'RZ09-0581' -or
    $plan.target.vendorIdHex -cne '1532' -or
    $plan.target.productIdHex -cne '02E0' -or
    $plan.target.bios -cne '3.01') {
    throw 'The reviewed capture plan is not bound to the exact admitted target.'
}
$planHash = (Get-FileHash -LiteralPath $resolvedPlan -Algorithm SHA256).Hash
if ($planHash -cne $ReviewedPlanSha256 -or
    $annotation.planSha256 -cne $ReviewedPlanSha256) {
    throw 'The annotation does not reference the exact reviewed capture plan.'
}
if ($annotation.target.model -cne $plan.target.model -or
    $annotation.target.modelNumber -cne $plan.target.modelNumber -or
    $annotation.target.vendorIdHex -cne '1532' -or
    $annotation.target.productIdHex -cne '02E0' -or
    $annotation.target.bios -cne '3.01' -or
    $annotation.evidenceProvenance.controller -cne 'Razer Synapse' -or
    $annotation.evidenceProvenance.role -cne 'OracleCapture' -or
    $annotation.evidenceProvenance.openBladeTypedApplyPerformed -ne $false -or
    $annotation.evidenceProvenance.openBladeVisualParityConfirmed -ne $false) {
    throw 'The annotation is not exact-target Synapse-oracle evidence.'
}
if (-not $annotation.gracefulStopConfirmed -or
    -not $annotation.decodable -or
    $annotation.pcap.rawCaptureCommitted -ne $false -or
    [string]$annotation.pcap.fileName -cne 'capture.pcap') {
    throw 'The annotation does not prove a graceful, decodable, non-committed raw capture.'
}

$captureItem = Get-Item -LiteralPath $resolvedCapture
$captureHash = (Get-FileHash -LiteralPath $resolvedCapture -Algorithm SHA256).Hash
if ([long]$annotation.pcap.byteLength -ne $captureItem.Length -or
    [string]$annotation.pcap.sha256 -cne $captureHash) {
    throw 'The PCAP size or SHA-256 does not match the finalized annotation.'
}

Assert-ReviewedActionSequence -Plan $plan -Annotation $annotation
$actions = @($annotation.actions)
$spectrumWindows = @(
    Get-ActionWindow -Actions $actions -Id 'spectrum-run-1'
    Get-ActionWindow -Actions $actions -Id 'spectrum-run-2'
    Get-ActionWindow -Actions $actions -Id 'spectrum-run-3'
)
$randomWindow = Get-ActionWindow -Actions $actions -Id 'breathing-random-long'

$filter = 'usb.device_address == {0} && usb.data_len == 382' -f [int]$annotation.deviceAddress
$rows = @(& $resolvedTshark `
    -n `
    -r $resolvedCapture `
    -Y $filter `
    -T fields `
    -E 'separator=/t' `
    -E 'occurrence=f' `
    -e frame.number `
    -e frame.time_epoch `
    -e usb.data_fragment)
if ($LASTEXITCODE -ne 0) {
    throw 'tshark failed while extracting target-device matrix reports.'
}
if ($rows.Count -eq 0) {
    throw 'No target-device matrix reports were found in the finalized PCAP.'
}

$analysisWindows = @($spectrumWindows) + @($randomWindow)
$boundedRows = @(foreach ($row in $rows) {
    $fields = $row -split "`t"
    if ($fields.Count -ne 3) {
        throw "Expected frame, epoch, and data fields; received: $row"
    }
    $epoch = [double]::Parse($fields[1], [Globalization.CultureInfo]::InvariantCulture)
    $inReviewedWindow = @($analysisWindows | Where-Object {
        $epoch -ge $_.Start -and $epoch -le $_.End
    }).Count -gt 0
    if ($inReviewedWindow) {
        $row
    }
})
if ($boundedRows.Count -eq 0) {
    throw 'No target-device matrix reports fell inside the reviewed action windows.'
}

$samples = @($boundedRows | ForEach-Object { ConvertTo-MatrixSample $_ } | Sort-Object Epoch, Frame)
$spectrumEvidence = @($spectrumWindows | ForEach-Object {
    Get-SpectrumRunEvidence -Window $_ -Samples $samples
})
$allPeriods = [double[]]@($spectrumEvidence | ForEach-Object {
    $_.completeCyclePeriodsMilliseconds
})
$randomEvidence = Get-RandomBreathingEvidence -Window $randomWindow -Samples $samples

$evidence = [ordered]@{
    schemaVersion = 1
    evidenceProvenance = [ordered]@{
        controller = 'Razer Synapse'
        role = 'OracleCaptureAnalysis'
        openBladeTypedApplyPerformed = $false
        openBladeVisualParityConfirmed = $false
    }
    target = [ordered]@{
        model = $plan.target.model
        modelNumber = $plan.target.modelNumber
        vendorIdHex = $plan.target.vendorIdHex
        productIdHex = $plan.target.productIdHex
        bios = $plan.target.bios
    }
    sourceCapture = [ordered]@{
        sanitizedFileName = 'capture.pcap'
        sha256 = $captureHash
        byteLength = $captureItem.Length
        rawCaptureCommitted = $false
        gracefulStopConfirmed = $true
        decodable = $true
    }
    protocol = [ordered]@{
        matrixCommand = '03/0B'
        matrixEnvelopeBytes = 374
        matrixSlotCount = 102
        sourceTargetMatrixFrameCount = $rows.Count
        extractedMatrixFrameCount = $samples.Count
    }
    method = [ordered]@{
        spectrum = 'Preserve every uniform RGB sample; convert RGB to HSV; unwrap each adjacent hue by the shortest angular delta; linearly interpolate only observed 360-degree boundary crossings.'
        randomBreathing = 'Partition uniform samples into non-black segments bounded by observed black reports; the exact maximum-channel samples in each bounded segment define its observed plateau color and timing.'
        timestamps = 'All emitted times are milliseconds relative to the visually confirmed action; absolute packet and action timestamps are omitted.'
    }
    spectrum = [ordered]@{
        runs = $spectrumEvidence
        completeCyclePeriodStatisticsAcrossRunsMilliseconds = Get-Statistics -Values $allPeriods
    }
    randomBreathing = $randomEvidence
    evidenceLimits = @(
        'The analyzer reports measured RGB samples and mathematical boundary crossings; it does not select, fit, or endorse a renderer curve.',
        'Random plateau frequencies and transitions are observations only; they do not identify a PRNG, seed, palette, or probability distribution.',
        'This Synapse-oracle evidence does not establish OpenBlade typed-apply, readback, restoration, or visual parity.'
    )
}

$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$temporaryPath = "$resolvedOutput.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporaryPath,
        ($evidence | ConvertTo-Json -Depth 30),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryPath, $resolvedOutput)
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

[pscustomobject]@{
    Status = 'Completed'
    EvidencePath = $resolvedOutput
    SpectrumRunCount = $spectrumEvidence.Count
    SpectrumCompleteCycleCount = $allPeriods.Count
    RandomBreathingBoundedCycleCount = $randomEvidence.boundedCycleCount
}
