[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$analyzer = Join-Path $repository 'captures\tools\Analyze-LightingSpectrumRandomOracle.ps1'
$planPath = Join-Path $repository 'captures\plans\2026-07-18-lighting-spectrum-random-oracle.json'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function New-MatrixReportHex {
    param(
        [Parameter(Mandatory)][string]$RgbHex,
        [Parameter(Mandatory)][ValidateRange(0, 255)][int]$TransactionId,
        [switch]$LightCapturedHole,
        [switch]$NonUniformCapturedKey,
        [switch]$CorruptChecksum
    )

    $red = [Convert]::ToByte($RgbHex.Substring(0, 2), 16)
    $green = [Convert]::ToByte($RgbHex.Substring(2, 2), 16)
    $blue = [Convert]::ToByte($RgbHex.Substring(4, 2), 16)
    $report = [byte[]]::new(374)
    $report[1] = [byte]$TransactionId
    $report[4] = 0x01
    $report[5] = 0x36
    $report[6] = 0x03
    $report[7] = 0x0B
    $report[11] = 0x65
    [int[]]$holeSlots = @(
        0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93)
    for ($slot = 0; $slot -lt 102; $slot++) {
        if ($holeSlots -contains $slot) {
            continue
        }
        $offset = 12 + ($slot * 3)
        $report[$offset] = $red
        $report[$offset + 1] = $green
        $report[$offset + 2] = $blue
    }
    if ($LightCapturedHole) {
        $report[12] = $red
        $report[13] = $green
        $report[14] = $blue
    }
    if ($NonUniformCapturedKey) {
        $report[15] = $report[15] -bxor 0x01
    }
    $checksum = [byte]0
    for ($offset = 2; $offset -lt 372; $offset++) {
        $checksum = $checksum -bxor $report[$offset]
    }
    $report[372] = $checksum
    if ($CorruptChecksum) {
        $report[372] = $report[372] -bxor 0x01
    }
    return ($report | ForEach-Object { '{0:X2}' -f $_ }) -join ''
}

function ConvertTo-EpochSeconds {
    param([Parameter(Mandatory)][string]$Timestamp)

    return [DateTimeOffset]::Parse($Timestamp).ToUnixTimeMilliseconds() / 1000.0
}

function New-Action {
    param(
        [Parameter(Mandatory)][object]$Step,
        [Parameter(Mandatory)][string]$Start
    )

    $confirmed = [DateTimeOffset]::Parse($Start)
    $completed = $confirmed.AddSeconds([int]$Step.holdSeconds)
    return [ordered]@{
        id = [string]$Step.id
        instruction = [string]$Step.instruction
        actionStartedAtUtc = $confirmed.AddSeconds(-1).ToString('O')
        actionConfirmedAtUtc = $confirmed.ToString('O')
        holdSeconds = [int]$Step.holdSeconds
        holdCompletedAtUtc = $completed.ToString('O')
        visuallyConfirmed = $true
    }
}

Assert-True (Test-Path -LiteralPath $analyzer -PathType Leaf) "Missing analyzer: $analyzer"
Assert-True (Test-Path -LiteralPath $planPath -PathType Leaf) "Missing plan: $planPath"

$source = Get-Content -LiteralPath $analyzer -Raw
Assert-True ($source -notmatch '(?i)USBPcapCMD|Start-UsbPcap|Stop-UsbPcap|GenerateConsoleCtrlEvent|CTRL_(?:C|BREAK)_EVENT') `
    'The offline analyzer must not launch or signal a capture process.'
Assert-True ($source -notmatch '(?i)Copy-Item|Move-Item.+CapturePath') `
    'The offline analyzer must not copy or move the raw PCAP.'
Assert-True ($source -match 'rawCaptureCommitted\s*=\s*\$false') `
    'The sanitized evidence must retain the raw-PCAP exclusion marker.'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('OpenBlade.LightingOracleAnalysis.Tests.' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $pcapPath = Join-Path $testRoot 'capture.pcap'
    $annotationPath = Join-Path $testRoot 'lighting-spectrum-random-annotations.json'
    $rowsPath = Join-Path $testRoot 'synthetic-matrix-rows.tsv'
    $fakeTshark = Join-Path $testRoot 'tshark.cmd'
    $tsharkArgumentsPath = Join-Path $testRoot 'tshark-arguments.txt'
    $evidencePath = Join-Path $testRoot 'sanitized-evidence.json'
    [IO.File]::WriteAllBytes($pcapPath, [byte[]](1..64))

    $runStarts = @(
        '2026-07-18T00:00:11.0000000Z',
        '2026-07-18T00:02:03.0000000Z',
        '2026-07-18T00:03:55.0000000Z'
    )
    $randomStart = '2026-07-18T00:05:47.0000000Z'
    [Collections.Generic.List[string]]$rows = [Collections.Generic.List[string]]::new()
    $frame = 1
    $transaction = 1
    $preWindowHex = New-MatrixReportHex -RgbHex '44D62C' `
        -TransactionId $transaction -NonUniformCapturedKey
    [void]$rows.Add(("{0}`t{1}`t{2}" -f
        $frame,
        (ConvertTo-EpochSeconds '2026-07-18T00:00:05.0000000Z').ToString(
            '0.000000', [Globalization.CultureInfo]::InvariantCulture),
        $preWindowHex))
    $frame++
    $transaction++
    $spectrumColors = @(
        'FF00FF', # 300 degrees
        'FF0080', # approximately 330 degrees
        'FF0000', # first 360/0-degree crossing
        'FF0080', # jitter back below the same boundary
        'FF0000', # recross the same boundary; not a complete cycle
        'FFFF00',
        '00FF00',
        '00FFFF',
        '0000FF',
        'FF00FF',
        'FF0000', # second 360-degree crossing
        'FFFF00'
    )
    foreach ($start in $runStarts) {
        $epoch = ConvertTo-EpochSeconds $start
        for ($index = 0; $index -lt $spectrumColors.Count; $index++) {
            $hex = New-MatrixReportHex -RgbHex $spectrumColors[$index] -TransactionId $transaction
            [void]$rows.Add(("{0}`t{1}`t{2}" -f
                $frame,
                ($epoch + ($index * 0.5)).ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture),
                $hex))
            $frame++
            $transaction = ($transaction + 1) % 256
        }
    }

    $randomFrames = @(
        @{ Offset = 0.0; Color = '000000' },
        @{ Offset = 0.5; Color = '000040' },
        @{ Offset = 1.0; Color = '000080' },
        @{ Offset = 1.5; Color = '000080' },
        @{ Offset = 2.0; Color = '000040' },
        @{ Offset = 2.5; Color = '000000' },
        @{ Offset = 3.5; Color = '007F7F' },
        @{ Offset = 4.0; Color = '00FFFF' },
        @{ Offset = 4.5; Color = '00FFFF' },
        @{ Offset = 5.0; Color = '007F7F' },
        @{ Offset = 5.5; Color = '000000' },
        @{ Offset = 6.5; Color = '000040' },
        @{ Offset = 7.0; Color = '000080' },
        @{ Offset = 7.5; Color = '000080' },
        @{ Offset = 8.0; Color = '000040' },
        @{ Offset = 8.5; Color = '000000' }
    )
    $randomEpoch = ConvertTo-EpochSeconds $randomStart
    foreach ($item in $randomFrames) {
        $hex = New-MatrixReportHex -RgbHex $item.Color -TransactionId $transaction
        [void]$rows.Add(("{0}`t{1}`t{2}" -f
            $frame,
            ($randomEpoch + [double]$item.Offset).ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture),
            $hex))
        $frame++
        $transaction = ($transaction + 1) % 256
    }
    [IO.File]::WriteAllLines($rowsPath, $rows, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $fakeTshark,
        "@echo %* > `"$tsharkArgumentsPath`"`r`n@type `"$rowsPath`"`r`n",
        [Text.ASCIIEncoding]::new())

    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    [string[]]$actionStarts = @(
        '2026-07-18T00:00:00.0000000Z',
        $runStarts[0],
        '2026-07-18T00:01:52.0000000Z',
        $runStarts[1],
        '2026-07-18T00:03:44.0000000Z',
        $runStarts[2],
        '2026-07-18T00:05:36.0000000Z',
        $randomStart,
        '2026-07-18T00:35:48.0000000Z',
        '2026-07-18T00:35:59.0000000Z')
    $actions = for ($index = 0; $index -lt @($plan.steps).Count; $index++) {
        New-Action -Step $plan.steps[$index] -Start $actionStarts[$index]
    }
    $annotation = [ordered]@{
        schemaVersion = 1
        planId = $plan.id
        planSha256 = (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
        evidenceProvenance = $plan.evidenceProvenance
        target = $plan.target
        startedAtUtc = '2026-07-17T23:59:58.0000000Z'
        completedAtUtc = '2026-07-18T00:36:10.0000000Z'
        deviceAddress = 2
        actions = $actions
        gracefulStopConfirmed = $true
        decodable = $true
        pcap = [ordered]@{
            fileName = 'capture.pcap'
            byteLength = (Get-Item -LiteralPath $pcapPath).Length
            sha256 = (Get-FileHash -LiteralPath $pcapPath -Algorithm SHA256).Hash
            rawCaptureCommitted = $false
        }
    }
    [IO.File]::WriteAllText(
        $annotationPath,
        ($annotation | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))

    $result = & $analyzer `
        -CapturePath $pcapPath `
        -AnnotationPath $annotationPath `
        -OutputPath $evidencePath `
        -TsharkPath $fakeTshark `
        -PlanPath $planPath
    Assert-True ($result.Status -ceq 'Completed') 'The synthetic offline analysis did not complete.'
    Assert-True (Test-Path -LiteralPath $evidencePath -PathType Leaf) 'The analyzer did not write evidence.'
    $tsharkArguments = Get-Content -LiteralPath $tsharkArgumentsPath -Raw
    Assert-True ($tsharkArguments -match 'usb\.device_address == 2') `
        'The analyzer did not constrain tshark to the annotated USB address.'
    Assert-True ($tsharkArguments -match 'usb\.data_len == 382') `
        'The analyzer did not constrain tshark to the captured matrix transfer length.'
    Assert-True ($tsharkArguments -match 'frame\.time_epoch') `
        'The analyzer did not request packet timestamps from tshark.'

    $evidenceText = Get-Content -LiteralPath $evidencePath -Raw
    $evidence = $evidenceText | ConvertFrom-Json
    Assert-True ($evidence.schemaVersion -eq 1) 'The evidence schema version is wrong.'
    Assert-True ($evidence.evidenceProvenance.role -ceq 'OracleCaptureAnalysis') `
        'The evidence does not retain its oracle-analysis boundary.'
    Assert-True ($evidence.sourceCapture.rawCaptureCommitted -eq $false) `
        'The evidence does not explicitly exclude the raw PCAP.'
    Assert-True ($evidence.sourceCapture.sha256 -ceq $annotation.pcap.sha256) `
        'The evidence does not retain the verified PCAP hash.'
    Assert-True ($evidence.protocol.sourceTargetMatrixFrameCount -eq 53) `
        'The analyzer did not account for the ignored pre-window matrix frame.'
    Assert-True ($evidence.protocol.extractedMatrixFrameCount -eq 52) `
        'The analyzer did not limit uniform validation to reviewed action windows.'
    Assert-True (@($evidence.spectrum.runs).Count -eq 3) 'The analyzer did not preserve all three Spectrum runs.'
    foreach ($run in @($evidence.spectrum.runs)) {
        Assert-True ($run.sampleCount -eq 12) 'A Spectrum run lost synthetic samples.'
        Assert-True (@($run.wrapCrossings).Count -eq 3) 'A Spectrum run did not expose all observed wrap crossings.'
        Assert-True ($run.ambiguousCrossingTransitionCount -eq 1) `
            'A repeated jitter crossing was not retained as ambiguous evidence.'
        Assert-True ($run.completeCycleCount -eq 1) 'A Spectrum run did not derive one crossing-to-crossing cycle.'
        Assert-True ([double]$run.completeCyclePeriodsMilliseconds[0] -eq 3000.0) `
            'A Spectrum run derived the wrong measured cycle period.'
        Assert-True ([double]$run.cadenceMilliseconds.mean -eq 500.0) `
            'A Spectrum run derived the wrong frame cadence.'
        Assert-True (@($run.curveSamples).Count -eq 12) `
            'A Spectrum run did not preserve its sanitized RGB curve samples.'
    }
    Assert-True ([double]$evidence.spectrum.completeCyclePeriodStatisticsAcrossRunsMilliseconds.mean -eq 3000.0) `
        'The cross-run Spectrum period summary is wrong.'

    $random = $evidence.randomBreathing
    Assert-True ($random.boundedCycleCount -eq 3) 'The analyzer did not find all bounded synthetic breaths.'
    Assert-True ((@($random.plateauSequenceRgbHex) -join ',') -ceq '000080,00FFFF,000080') `
        'The Random Breathing plateau sequence is wrong.'
    Assert-True ($random.distinctPlateauColorCount -eq 2) `
        'The Random Breathing color count is wrong.'
    Assert-True ((@($random.peakToPeakIntervalsMilliseconds) -join ',') -ceq '3000,3000') `
        'The Random Breathing cycle cadence is wrong.'
    Assert-True (@($random.transitions).Count -eq 2) `
        'The Random Breathing transition summary is wrong.'

    Assert-True ($evidenceText -notmatch [regex]::Escape($testRoot)) `
        'Sanitized evidence leaked a host path.'
    Assert-True ($evidenceText -notmatch '(?i)processId|processGroupId|usbPcapDevice|deviceAddress') `
        'Sanitized evidence leaked a process or capture-device identifier.'
    $rawReportMarker = (New-MatrixReportHex -RgbHex 'FF00FF' -TransactionId 1).Substring(0, 96)
    Assert-True ($evidenceText.IndexOf($rawReportMarker, [StringComparison]::Ordinal) -lt 0) `
        'Sanitized evidence leaked a raw matrix report.'

    # A valid capture can establish that no complete wrap or only one breath was
    # observed. Windows PowerShell 5.1 must serialize honest zero-count evidence
    # instead of rejecting an empty statistics input.
    [Collections.Generic.List[string]]$zeroRows = [Collections.Generic.List[string]]::new()
    $zeroFrame = 1
    $zeroTransaction = 1
    foreach ($start in $runStarts) {
        $epoch = ConvertTo-EpochSeconds $start
        foreach ($item in @(
            @{ Offset = 0.0; Color = 'FF0000' },
            @{ Offset = 0.5; Color = 'FF4000' },
            @{ Offset = 1.0; Color = 'FF8000' })) {
            $hex = New-MatrixReportHex -RgbHex $item.Color -TransactionId $zeroTransaction
            [void]$zeroRows.Add(("{0}`t{1}`t{2}" -f
                $zeroFrame,
                ($epoch + [double]$item.Offset).ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture),
                $hex))
            $zeroFrame++
            $zeroTransaction = ($zeroTransaction + 1) % 256
        }
    }
    $randomEpoch = ConvertTo-EpochSeconds $randomStart
    foreach ($item in @(
        @{ Offset = 0.0; Color = '000000' },
        @{ Offset = 0.5; Color = '000040' },
        @{ Offset = 1.0; Color = '000080' },
        @{ Offset = 1.5; Color = '000040' },
        @{ Offset = 2.0; Color = '000000' })) {
        $hex = New-MatrixReportHex -RgbHex $item.Color -TransactionId $zeroTransaction
        [void]$zeroRows.Add(("{0}`t{1}`t{2}" -f
            $zeroFrame,
            ($randomEpoch + [double]$item.Offset).ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture),
            $hex))
        $zeroFrame++
        $zeroTransaction = ($zeroTransaction + 1) % 256
    }
    [IO.File]::WriteAllLines($rowsPath, $zeroRows, [Text.UTF8Encoding]::new($false))
    $zeroEvidencePath = Join-Path $testRoot 'zero-cycle-evidence.json'
    & $analyzer `
        -CapturePath $pcapPath `
        -AnnotationPath $annotationPath `
        -OutputPath $zeroEvidencePath `
        -TsharkPath $fakeTshark `
        -PlanPath $planPath | Out-Null
    $zeroEvidence = Get-Content -LiteralPath $zeroEvidencePath -Raw | ConvertFrom-Json
    foreach ($run in @($zeroEvidence.spectrum.runs)) {
        Assert-True ($run.completeCycleCount -eq 0) `
            'A no-wrap Spectrum window reported a complete cycle.'
        Assert-True ($run.completeCyclePeriodStatisticsMilliseconds.count -eq 0) `
            'A no-wrap Spectrum window did not emit zero-count period statistics.'
    }
    Assert-True ($zeroEvidence.spectrum.completeCyclePeriodStatisticsAcrossRunsMilliseconds.count -eq 0) `
        'No-wrap cross-run evidence did not emit zero-count period statistics.'
    Assert-True ($zeroEvidence.randomBreathing.boundedCycleCount -eq 1) `
        'The one-breath fixture did not retain its single bounded cycle.'
    Assert-True ($zeroEvidence.randomBreathing.peakToPeakStatisticsMilliseconds.count -eq 0) `
        'One-breath evidence did not emit zero-count interval statistics.'

    [string[]]$invalidMaskRows = @($rows)
    $firstFields = $invalidMaskRows[1] -split "`t"
    $invalidMaskRows[1] = "{0}`t{1}`t{2}" -f `
        $firstFields[0],
        $firstFields[1],
        (New-MatrixReportHex -RgbHex 'FF00FF' -TransactionId 2 -LightCapturedHole)
    [IO.File]::WriteAllLines(
        $rowsPath,
        $invalidMaskRows,
        [Text.UTF8Encoding]::new($false))
    $invalidMaskRejected = $false
    try {
        & $analyzer `
            -CapturePath $pcapPath `
            -AnnotationPath $annotationPath `
            -OutputPath (Join-Path $testRoot 'invalid-mask-evidence.json') `
            -TsharkPath $fakeTshark `
            -PlanPath $planPath | Out-Null
    }
    catch {
        $invalidMaskRejected = $_.Exception.Message -match 'layout-hole slot'
    }
    Assert-True $invalidMaskRejected `
        'The analyzer accepted a matrix report that lit a captured hole slot.'

    [string[]]$invalidChecksumRows = @($rows)
    $invalidChecksumRows[1] = "{0}`t{1}`t{2}" -f `
        $firstFields[0],
        $firstFields[1],
        (New-MatrixReportHex -RgbHex 'FF00FF' -TransactionId 2 -CorruptChecksum)
    [IO.File]::WriteAllLines(
        $rowsPath,
        $invalidChecksumRows,
        [Text.UTF8Encoding]::new($false))
    $invalidChecksumRejected = $false
    try {
        & $analyzer `
            -CapturePath $pcapPath `
            -AnnotationPath $annotationPath `
            -OutputPath (Join-Path $testRoot 'invalid-checksum-evidence.json') `
            -TsharkPath $fakeTshark `
            -PlanPath $planPath | Out-Null
    }
    catch {
        $invalidChecksumRejected = $_.Exception.Message -match 'checksum'
    }
    Assert-True $invalidChecksumRejected `
        'The analyzer accepted a matrix report with an invalid checksum.'

    $sequenceAnnotationPath = Join-Path $testRoot 'invalid-sequence-annotations.json'
    $annotation.actions[1].holdSeconds = 99
    [IO.File]::WriteAllText(
        $sequenceAnnotationPath,
        ($annotation | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $invalidSequenceRejected = $false
    try {
        & $analyzer `
            -CapturePath $pcapPath `
            -AnnotationPath $sequenceAnnotationPath `
            -OutputPath (Join-Path $testRoot 'invalid-sequence-evidence.json') `
            -TsharkPath $fakeTshark `
            -PlanPath $planPath | Out-Null
    }
    catch {
        $invalidSequenceRejected = $_.Exception.Message -match 'reviewed plan sequence'
    }
    Assert-True $invalidSequenceRejected `
        'The analyzer accepted an annotation that changed a reviewed hold duration.'
    $annotation.actions[1].holdSeconds = 100

    $unboundedAnnotationPath = Join-Path $testRoot 'unbounded-annotations.json'
    $annotation.actions[9].holdCompletedAtUtc = '2026-07-18T00:36:19.0000000Z'
    $annotation.completedAtUtc = '2026-07-18T00:36:20.0000000Z'
    [IO.File]::WriteAllText(
        $unboundedAnnotationPath,
        ($annotation | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $unboundedRejected = $false
    try {
        & $analyzer `
            -CapturePath $pcapPath `
            -AnnotationPath $unboundedAnnotationPath `
            -OutputPath (Join-Path $testRoot 'unbounded-evidence.json') `
            -TsharkPath $fakeTshark `
            -PlanPath $planPath | Out-Null
    }
    catch {
        $unboundedRejected = $_.Exception.Message -match 'unbounded'
    }
    Assert-True $unboundedRejected `
        'The analyzer accepted an observation window longer than the reviewed hold.'
    $annotation.actions[9].holdCompletedAtUtc = '2026-07-18T00:36:09.0000000Z'
    $annotation.completedAtUtc = '2026-07-18T00:36:10.0000000Z'

    $tamperedPlanPath = Join-Path $testRoot 'tampered-plan.json'
    $tamperedPlan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    $tamperedPlan.steps[1].instruction = 'A modified instruction that was never reviewed.'
    [IO.File]::WriteAllText(
        $tamperedPlanPath,
        ($tamperedPlan | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $tamperedPlanAnnotationPath = Join-Path $testRoot 'tampered-plan-annotations.json'
    $annotation.planSha256 =
        (Get-FileHash -LiteralPath $tamperedPlanPath -Algorithm SHA256).Hash
    $annotation.actions[1].instruction = $tamperedPlan.steps[1].instruction
    [IO.File]::WriteAllText(
        $tamperedPlanAnnotationPath,
        ($annotation | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $tamperedPlanRejected = $false
    try {
        & $analyzer `
            -CapturePath $pcapPath `
            -AnnotationPath $tamperedPlanAnnotationPath `
            -OutputPath (Join-Path $testRoot 'tampered-plan-evidence.json') `
            -TsharkPath $fakeTshark `
            -PlanPath $tamperedPlanPath | Out-Null
    }
    catch {
        $tamperedPlanRejected = $_.Exception.Message -match 'exact reviewed capture plan'
    }
    Assert-True $tamperedPlanRejected `
        'The analyzer accepted a modified plan with a matching annotation hash.'
    $annotation.planSha256 = (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
    $annotation.actions[1].instruction = $plan.steps[1].instruction

    $tamperedAnnotationPath = Join-Path $testRoot 'tampered-annotations.json'
    $tamperedOutputPath = Join-Path $testRoot 'tampered-evidence.json'
    $annotation.pcap.sha256 = ('0' * 64)
    [IO.File]::WriteAllText(
        $tamperedAnnotationPath,
        ($annotation | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $tamperRejected = $false
    try {
        & $analyzer `
            -CapturePath $pcapPath `
            -AnnotationPath $tamperedAnnotationPath `
            -OutputPath $tamperedOutputPath `
            -TsharkPath $fakeTshark `
            -PlanPath $planPath | Out-Null
    }
    catch {
        $tamperRejected = $_.Exception.Message -match 'SHA-256'
    }
    Assert-True $tamperRejected 'The analyzer accepted a PCAP whose annotation hash was tampered.'
    Assert-True (-not (Test-Path -LiteralPath $tamperedOutputPath)) `
        'The analyzer wrote evidence after rejecting a tampered PCAP.'
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

Write-Host 'Lighting Spectrum/Random oracle-analysis tests passed.'
