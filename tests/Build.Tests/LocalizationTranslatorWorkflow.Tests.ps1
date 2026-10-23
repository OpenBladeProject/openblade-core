[CmdletBinding()]
param([switch]$ArchiveEvidenceOnly)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$modulePath = Join-Path $repository 'build\LocalizationTranslatorWorkflow.psm1'
Import-Module $modulePath -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Assert-ArchiveEqual {
    param(
        [string]$Reference,
        [string]$Actual,
        [string]$Stage,
        [string]$EvidenceRoot,
        [string]$Message)

    $expectedHash = (Get-FileHash -LiteralPath $Reference -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash
    if ($expectedHash -ceq $actualHash) { return }

    $failure = "$Message Expected '$expectedHash', actual '$actualHash'."
    $destination = Join-Path $EvidenceRoot ([Guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        Copy-Item -LiteralPath $Reference -Destination (Join-Path $destination 'reference.zip')
        Copy-Item -LiteralPath $Actual -Destination (Join-Path $destination 'actual.zip')
        [ordered]@{
            stage = $Stage
            referenceSha256 = $expectedHash
            actualSha256 = $actualHash
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'comparison.json') -Encoding UTF8
        $failure += " Archive evidence retained at: $destination"
    }
    catch {
        # Evidence I/O must never hide or turn the original mismatch into success.
        $failure += " Evidence retention failed: $($_.Exception.Message)"
    }
    throw $failure
}

function Assert-Throws {
    param([string]$Pattern, [scriptblock]$Action, [string]$Message)
    try { & $Action }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "$Message Wrong error: $($_.Exception.Message)"
        }
        return
    }
    throw "$Message Expected an exception matching '$Pattern'."
}

function Write-CsvRows {
    param([Parameter(Mandatory)]$Rows, [Parameter(Mandatory)][string]$Path)
    [IO.File]::WriteAllLines(
        $Path,
        @($Rows | ConvertTo-Csv -NoTypeInformation),
        [Text.UTF8Encoding]::new($true))
}

function Copy-FixtureFile {
    param([string]$Root, [string]$Relative)
    $destination = Join-Path $Root $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) |
        Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $Relative) -Destination $destination
}

function New-TranslatorFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) (
        'OpenBlade.Translator.' + [Guid]::NewGuid().ToString('N'))
    foreach ($relative in @(
            'build\LocalizationTranslatorWorkflow.psm1',
            'build\localization\TranslatorScreenshotMap.json',
            'src\OpenBlade.Tray\Strings\en-US\Resources.resw',
            'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw',
            'src\OpenBlade.Tray.Core\Properties\Resources.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.ru-RU.resx',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.ru-RU.resx',
            'src\OpenBlade.Setup\Properties\Resources.resx',
            'src\OpenBlade.Setup\Properties\Resources.ru-RU.resx')) {
        Copy-FixtureFile $root $relative
    }
    $validator = Join-Path $root 'build\Validate-LocalizationResources.ps1'
    [IO.File]::WriteAllText(
        $validator,
        "[CmdletBinding()]`r`nparam([string]`$Repository, [switch]`$AllowLockedCatalogRead)`r`n",
        [Text.UTF8Encoding]::new($false))
    return $root
}

function Get-CatalogPaths {
    param([string]$Root)
    return @(
        'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw',
        'src\OpenBlade.Tray.Core\Properties\Resources.ru-RU.resx',
        'build\installer\OpenBlade.Installer.UI\Properties\Resources.ru-RU.resx',
        'src\OpenBlade.Setup\Properties\Resources.ru-RU.resx') |
        ForEach-Object { Join-Path $Root $_ }
}

function Get-AuthoritativeSourceRowCount {
    $total = 0
    foreach ($relative in @(
            'src\OpenBlade.Tray\Strings\en-US\Resources.resw',
            'src\OpenBlade.Tray.Core\Properties\Resources.resx',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx',
            'src\OpenBlade.Setup\Properties\Resources.resx')) {
        [xml]$catalog = [IO.File]::ReadAllText((Join-Path $repository $relative))
        $total += @($catalog.root.data).Count
    }
    return $total
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade.Translator.Tests.' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    # Exercise the failure path deterministically, without waiting for a renderer mismatch.
    $probeRoot = Join-Path $testRoot 'archive-evidence-probe'
    [IO.Directory]::CreateDirectory($probeRoot) | Out-Null
    $referenceProbe = Join-Path $probeRoot 'reference.zip'
    $actualProbe = Join-Path $probeRoot 'actual.zip'
    [IO.File]::WriteAllBytes($referenceProbe, [byte[]](1, 2, 3))
    Copy-Item -LiteralPath $referenceProbe -Destination $actualProbe
    $probeEvidence = Join-Path $testRoot 'retained-evidence'
    Assert-ArchiveEqual -Reference $referenceProbe -Actual $actualProbe `
        -Stage 'same-process' -EvidenceRoot $probeEvidence -Message 'Probe mismatch.'
    Assert-True (-not (Test-Path -LiteralPath $probeEvidence)) `
        'Matching archive comparisons must not create retained artifacts.'
    [IO.File]::WriteAllBytes($actualProbe, [byte[]](3, 2, 1))
    foreach ($stage in @('same-process', 'fresh-process')) {
        Assert-Throws 'Probe mismatch.*Archive evidence retained at:' {
            Assert-ArchiveEqual -Reference $referenceProbe -Actual $actualProbe `
                -Stage $stage -EvidenceRoot $probeEvidence -Message 'Probe mismatch.'
        } 'An archive mismatch must remain a failure after retaining evidence.'
    }
    $retained = @(Get-ChildItem -LiteralPath $probeEvidence -Directory)
    Assert-Equal 2 $retained.Count 'Each mismatch must have an independent evidence directory.'
    $referenceProbeHash = (Get-FileHash -LiteralPath $referenceProbe -Algorithm SHA256).Hash
    $actualProbeHash = (Get-FileHash -LiteralPath $actualProbe -Algorithm SHA256).Hash
    $blockedEvidence = Join-Path $testRoot 'blocked-evidence'
    [IO.File]::WriteAllText($blockedEvidence, 'This file prevents directory creation.')
    Assert-Throws 'Probe mismatch.*Expected.*actual.*Evidence retention failed:' {
        Assert-ArchiveEqual -Reference $referenceProbe -Actual $actualProbe `
            -Stage 'fresh-process' -EvidenceRoot $blockedEvidence -Message 'Probe mismatch.'
    } 'Evidence I/O failure must preserve the original archive mismatch.'
    $resolvedProbe = [IO.Path]::GetFullPath($probeRoot)
    if (-not $resolvedProbe.StartsWith(
            [IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected archive-probe cleanup target: $resolvedProbe"
    }
    Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
    $retainedStages = @()
    foreach ($directory in $retained) {
        $comparison = Get-Content (Join-Path $directory.FullName 'comparison.json') -Raw |
            ConvertFrom-Json
        $retainedStages += $comparison.stage
        Assert-Equal $referenceProbeHash $comparison.referenceSha256 'Reference hash must be recorded.'
        Assert-Equal $actualProbeHash $comparison.actualSha256 'Actual hash must be recorded.'
        Assert-Equal $referenceProbeHash `
            (Get-FileHash (Join-Path $directory.FullName 'reference.zip') -Algorithm SHA256).Hash `
            'Reference bytes must survive source-fixture cleanup.'
        Assert-Equal $actualProbeHash `
            (Get-FileHash (Join-Path $directory.FullName 'actual.zip') -Algorithm SHA256).Hash `
            'Mismatching bytes must survive source-fixture cleanup.'
    }
    Assert-True ($retainedStages -contains 'same-process' -and $retainedStages -contains 'fresh-process') `
        'Evidence must distinguish same-process and fresh-process comparisons.'
    if ($ArchiveEvidenceOnly) {
        Write-Host 'Translator archive evidence tests passed.'
        return
    }

    $first = Join-Path $testRoot 'first.csv'
    $second = Join-Path $testRoot 'second.csv'
    $model = Export-LocalizationWorkbook -Repository $repository -Locale 'ru-RU' -Path $first
    [void](Export-LocalizationWorkbook -Repository $repository -Locale 'ru-RU' -Path $second)
    $expectedRowCount = Get-AuthoritativeSourceRowCount
    Assert-Equal $expectedRowCount @($model.Rows).Count `
        'The workbook must cover every row in all four catalogs.'
    Assert-Equal 4 @($model.Rows | Group-Object Surface).Count `
        'The workbook must contain four distinct surfaces.'
    Assert-True (@($model.Rows | Where-Object {
                $_.FormatVersion -cne '2' -or
                $_.TargetCatalogHash -notmatch '^[0-9a-f]{64}$'
            }).Count -eq 0) `
        'Every format-2 row must bind the exact target-catalog state.'
    Assert-Equal (Get-FileHash $first -Algorithm SHA256).Hash `
        (Get-FileHash $second -Algorithm SHA256).Hash `
        'Repeated exports must be byte deterministic.'

    $cultureEnglish = Join-Path $testRoot 'culture-en-US.csv'
    $cultureTurkish = Join-Path $testRoot 'culture-tr-TR.csv'
    $previousCulture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture =
            [Globalization.CultureInfo]::GetCultureInfo('en-US')
        [void](Export-LocalizationWorkbook `
            -Repository $repository -Locale 'ru-RU' -Path $cultureEnglish)
        [Globalization.CultureInfo]::CurrentCulture =
            [Globalization.CultureInfo]::GetCultureInfo('tr-TR')
        [void](Export-LocalizationWorkbook `
            -Repository $repository -Locale 'ru-RU' -Path $cultureTurkish)
    }
    finally {
        [Globalization.CultureInfo]::CurrentCulture = $previousCulture
    }
    Assert-Equal (Get-FileHash $cultureEnglish -Algorithm SHA256).Hash `
        (Get-FileHash $cultureTurkish -Algorithm SHA256).Hash `
        'Workbook context matching must be culture invariant.'

    $prefix = [IO.File]::ReadAllBytes($first)[0..2]
    Assert-Equal '239 187 191' ($prefix -join ' ') `
        'The CSV must include the UTF-8 BOM expected by Excel.'
    $rows = @(Import-Csv -LiteralPath $first)
    Assert-True (@($rows | Where-Object {
                [string]::IsNullOrWhiteSpace($_.Screenshots)
            }).Count -eq 0) 'Every workbook row must map to context screenshots.'
    Assert-True (@($rows | Where-Object {
                $_.Screenshots -match '(?:^|;)context/(?:tray|installer)/[^/]+\.png(?:;|$)'
            }).Count -eq $expectedRowCount) `
        'Screenshot references must be safe package-relative PNG paths.'
    $profileContext = @($rows | Where-Object Key -ceq `
        'MainWindow_ButtonExportProfiles.Content')[0]
    foreach ($profileScreenshot in @(
            'context/tray/main-profiles.png',
            'context/tray/main-profiles-narrow.png',
            'context/tray/main-profiles-wide.png')) {
        Assert-True ($profileContext.Screenshots.Split(';') -ccontains $profileScreenshot) `
            "Profile rows must reference '$profileScreenshot'."
    }
    foreach ($featureContext in @(
            @{
                Key = 'Applications_Title.Text'
                Screenshots = @(
                    'context/tray/main-profiles-applications.png',
                    'context/tray/main-profiles-applications-empty.png',
                    'context/tray/main-profiles-application-editor.png',
                    'context/tray/main-profiles-application-editor-controls.png',
                    'context/tray/main-profiles-application-discovery.png',
                    'context/tray/main-profiles-application-discovery-empty.png')
            },
            @{
                Key = 'ApplicationsUI_SearchLabel'
                Screenshots = @(
                    'context/tray/main-profiles-application-discovery.png',
                    'context/tray/main-profiles-application-discovery-empty.png')
            },
            @{
                Key = 'Telemetry_Title.Text'
                Screenshots = @(
                    'context/tray/main-telemetry.png',
                    'context/tray/main-telemetry-empty.png',
                    'context/tray/main-telemetry-controls-narrow.png',
                    'context/tray/main-telemetry-hover-edge.png',
                    'context/tray/main-telemetry-recording.png',
                    'context/tray/main-telemetry-replay.png')
            },
            @{
                Key = 'BatteryHistory_Title.Text'
                Screenshots = @('context/tray/main-laptop-battery-history.png')
            },
            @{
                Key = 'TelemetryUI_NextSample'
                Screenshots = @(
                    'context/tray/main-telemetry.png',
                    'context/tray/main-telemetry-empty.png',
                    'context/tray/main-telemetry-controls-narrow.png',
                    'context/tray/main-telemetry-hover-edge.png',
                    'context/tray/main-telemetry-recording.png',
                    'context/tray/main-telemetry-replay.png')
            },
            @{
                Key = 'MainWindow_DisplayBrightnessTitle.Text'
                Screenshots = @(
                    'context/tray/main-display-brightness-known.png',
                    'context/tray/main-display-brightness-unavailable.png')
            },
            @{
                Key = 'Application profiles'
                Screenshots = @(
                    'context/tray/main-profiles-applications.png',
                    'context/tray/main-profiles-applications-empty.png',
                    'context/tray/main-profiles-application-editor.png')
            },
            @{
                Key = 'Telemetry recording reached its limit and was saved to {0}.'
                Screenshots = @(
                    'context/tray/main-telemetry.png',
                    'context/tray/main-telemetry-empty.png',
                    'context/tray/main-telemetry-recording.png')
            },
            @{
                Key = 'Battery history'
                Screenshots = @('context/tray/main-laptop-battery-history.png')
            },
            @{
                Key = 'Display brightness'
                Screenshots = @(
                    'context/tray/main-display-brightness-known.png',
                    'context/tray/main-display-brightness-unavailable.png')
            })) {
        $featureRow = @($rows | Where-Object Key -ceq $featureContext.Key)[0]
        foreach ($featureScreenshot in $featureContext.Screenshots) {
            Assert-True ($featureRow.Screenshots.Split(';') -ccontains $featureScreenshot) `
                "'$($featureContext.Key)' must reference '$featureScreenshot'."
        }
    }
    $snapTapContext = @($rows | Where-Object Key -ceq 'SnapTap_InfoInput.Text')[0]
    foreach ($snapTapScreenshot in @(
            'context/tray/main-laptop-snap-tap-info.png',
            'context/tray/snap-tap-information.png')) {
        Assert-True ($snapTapContext.Screenshots.Split(';') -ccontains $snapTapScreenshot) `
            "SnapTap help must reference '$snapTapScreenshot'."
    }
    $taglineContext = @($rows | Where-Object Key -ceq `
        'MainWindow_TextBlockForGamersByTheCommunity.Text')[0]
    Assert-Equal 'context/tray/main-about.png' $taglineContext.Screenshots `
        'The community tagline must use the About-page screenshot.'

    $reordered = Join-Path $testRoot 'reordered.csv'
    Write-CsvRows @($rows | Sort-Object Surface, Key -Descending) $reordered
    $validated = Import-LocalizationWorkbook `
        -Repository $repository -Path $reordered -ValidateOnly
    Assert-Equal $expectedRowCount $validated.RowCount `
        'Reordered rows must validate by stable identity.'

    Assert-Throws 'cannot be used as a translator target' {
        Export-LocalizationWorkbook -Repository $repository -Locale 'qps-ploc' `
            -Path (Join-Path $testRoot 'qps.csv')
    } 'Pseudo-localization must not be exportable.'
    Assert-Throws 'not an admitted production translation locale' {
        Export-LocalizationWorkbook -Repository $repository -Locale 'fr-FR' `
            -Path (Join-Path $testRoot 'fr-FR.csv')
    } 'Translator tooling must not admit a new production locale implicitly.'

    $cases = @(
        @('duplicate', 'duplicate row', {
            param($copy)
            return @($copy) + $copy[0]
        }),
        @('missing', 'row count', {
            param($copy)
            return @($copy | Select-Object -Skip 1)
        }),
        @('source-tamper', 'stale or tampered.*Source', {
            param($copy)
            $copy[0].Source += ' tampered'
            return $copy
        }),
        @('hash-tamper', 'stale or tampered.*SourceHash', {
            param($copy)
            $copy[0].SourceHash = '0' * 64
            return $copy
        }),
        @('catalog-tamper', 'stale or tampered.*CatalogHash', {
            param($copy)
            $copy[0].CatalogHash = '0' * 64
            return $copy
        }),
        @('target-catalog-tamper', 'stale or tampered.*TargetCatalogHash', {
            param($copy)
            $copy[0].TargetCatalogHash = '0' * 64
            return $copy
        }),
        @('mixed-locale', 'mixes locales', {
            param($copy)
            $copy[0].Locale = 'zh-CN'
            return $copy
        }),
        @('formula', 'spreadsheet formula character', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = '=1+1'
            return $copy
        }),
        @('placeholder', 'format placeholders', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like '*Keep format items*')[0]
            $candidate.Translation = 'placeholder removed'
            return $copy
        }),
        @('placeholder-alignment-whitespace', 'format placeholders', {
            param($copy)
            $candidate = @($copy | Where-Object Source -ceq '{0:N0} RPM')[0]
            $candidate.Translation = '{1, 10} ' + $candidate.Translation
            return $copy
        }),
        @('placeholder-escaped-wrapper', 'format placeholders', {
            param($copy)
            $candidate = @($copy | Where-Object Source -ceq '{0:N0} RPM')[0]
            $candidate.Translation = '{{{1}}} ' + $candidate.Translation
            return $copy
        }),
        @('format-index-limit', 'invalid .NET format string', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = '{99999999999}'
            return $copy
        }),
        @('shortcut-token', 'protected token', {
            param($copy)
            $candidate = @($copy | Where-Object Key -ceq `
                'SpecialKey_ValuePlaceholder_Shortcut')[0]
            $candidate.Translation = $candidate.Translation.Replace(
                'Ctrl+Shift+M', 'Ctrl+Shift+N')
            return $copy
        }),
        @('named-shortcut-token', 'protected token', {
            param($copy)
            $candidate = @($copy | Where-Object {
                    $_.Surface -ceq 'tray-core' -and $_.Key -ceq 'Block Alt+Tab'
                })[0]
            $candidate.Translation = $candidate.Translation.Replace(
                'Alt+Tab', 'Alt+Teb')
            return $copy
        }),
        @('windows-path-token', 'protected token', {
            param($copy)
            $candidate = @($copy | Where-Object Key -ceq `
                'SpecialKey_ValuePlaceholder_AppPath')[0]
            $candidate.Translation = $candidate.Translation.Replace(
                'C:\Path\To\App.exe', 'C:\Other\App.exe')
            return $copy
        }),
        @('special-key-token', 'protected token', {
            param($copy)
            $candidate = @($copy | Where-Object Key -ceq 'SpecialKey_Slot_M1')[0]
            $candidate.Translation = 'M6'
            return $copy
        }),
        @('whitespace', 'leading or trailing whitespace', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like '*leading and trailing*')[0]
            $candidate.Translation = $candidate.Translation.Trim()
            return $copy
        }),
        @('line-break', 'line-break count', {
            param($copy)
            $candidate = @($copy | Where-Object {
                    $_.Constraints -like '*explicit line break*' -and
                    $_.Constraints -notlike '*leading and trailing*'
                })[0]
            $candidate.Translation = $candidate.Translation.Replace("`r`n", ' ').Replace("`n", ' ')
            return $copy
        }),
        @('control', 'forbidden control character', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation += [char]7
            return $copy
        }),
        @('lone-cr', 'forbidden control character or line separator', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = $candidate.Translation.Insert(1, [string][char]13)
            return $copy
        }),
        @('c1-control', 'forbidden control character or line separator', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = $candidate.Translation.Insert(1, [string][char]0x85)
            return $copy
        }),
        @('unicode-line-separator', 'forbidden control character or line separator', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = $candidate.Translation.Insert(1, [string][char]0x2028)
            return $copy
        }),
        @('empty-translation', 'is blank', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = ''
            return $copy
        }),
        @('zero-width-space', 'forbidden control character or line separator', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = [string][char]0x200B
            return $copy
        }),
        @('bidi-override', 'forbidden control character or line separator', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = $candidate.Translation.Insert(1, [string][char]0x202E)
            return $copy
        }),
        @('zero-width-joiner-only', 'is blank', {
            param($copy)
            $candidate = @($copy | Where-Object Constraints -like 'Translate naturally*')[0]
            $candidate.Translation = [string][char]0x200D
            return $copy
        }))
    foreach ($case in $cases) {
        $copy = @(Import-Csv -LiteralPath $first)
        $mutated = & $case[2] $copy
        $path = Join-Path $testRoot ($case[0] + '.csv')
        Write-CsvRows $mutated $path
        Assert-Throws $case[1] {
            Import-LocalizationWorkbook -Repository $repository -Path $path -ValidateOnly
        } "The $($case[0]) workbook must fail closed."
    }

    $accessCollision = Join-Path $testRoot 'access-collision.csv'
    $accessRows = @(Import-Csv -LiteralPath $first)
    @($accessRows | Where-Object {
            $_.Surface -ceq 'installer' -and $_.Key -ceq '_Install'
        })[0].Translation = '_Apply'
    @($accessRows | Where-Object {
            $_.Surface -ceq 'installer' -and $_.Key -ceq '_Cancel'
        })[0].Translation = '_Abort'
    Write-CsvRows $accessRows $accessCollision
    Assert-Throws 'access key.*collides' {
        Import-LocalizationWorkbook `
            -Repository $repository -Path $accessCollision -ValidateOnly
    } 'Simultaneously visible installer actions must use distinct access keys.'

    $accessGroupFixture = New-TranslatorFixture
    try {
        Import-Module (Join-Path $accessGroupFixture `
            'build\LocalizationTranslatorWorkflow.psm1') -Force
        foreach ($relative in @(
                'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx',
                'build\installer\OpenBlade.Installer.UI\Properties\Resources.ru-RU.resx')) {
            $path = Join-Path $accessGroupFixture $relative
            [xml]$document = [IO.File]::ReadAllText($path)
            $node = @($document.root.data | Where-Object {
                    [string]$_.name -ceq '_Install'
                })[0]
            [void]$document.root.RemoveChild($node)
            $document.Save($path)
        }
        $accessGroupWorkbook = Join-Path $testRoot 'access-group-drift.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $accessGroupFixture -Locale 'ru-RU' -Path $accessGroupWorkbook)
        Assert-Throws 'references missing resource.*installer/_Install' {
            Import-LocalizationWorkbook `
                -Repository $accessGroupFixture -Path $accessGroupWorkbook -ValidateOnly
        } 'Access-key groups must fail closed when an authoritative key is renamed.'
    }
    finally {
        Import-Module $modulePath -Force
        Remove-Item -LiteralPath $accessGroupFixture -Recurse -Force
    }

    $screenshotFixture = Join-Path $testRoot 'screenshot-set'
    $screenshotMap = Get-Content -LiteralPath (Join-Path $repository `
        'build\localization\TranslatorScreenshotMap.json') -Raw | ConvertFrom-Json
    foreach ($relative in @($screenshotMap.screenshots | ForEach-Object { [string]$_ })) {
        $path = Join-Path $screenshotFixture $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllBytes($path, [byte[]]@(1))
    }
    Test-LocalizationScreenshotSet `
        -Repository $repository -ContextDirectory $screenshotFixture
    $unexpectedScreenshot = Join-Path $screenshotFixture 'tray\unexpected.png'
    [IO.File]::WriteAllBytes($unexpectedScreenshot, [byte[]]@(1))
    Assert-Throws 'not declared in the map' {
        Test-LocalizationScreenshotSet `
            -Repository $repository -ContextDirectory $screenshotFixture
    } 'Generated screenshots must be explicitly declared before packaging.'

    $badHeaders = Join-Path $testRoot 'headers.csv'
    Write-CsvRows @($rows | Select-Object -Property FormatVersion, Locale, Surface, Key,
        Source, Translation, Context, Constraints, Screenshots, SourceHash) $badHeaders
    Assert-Throws 'headers must be exactly' {
        Import-LocalizationWorkbook -Repository $repository -Path $badHeaders -ValidateOnly
    } 'Structurally incomplete headers must fail closed.'

    $extraField = Join-Path $testRoot 'extra-field.csv'
    $extraFieldLines = [Collections.Generic.List[string]]::new()
    $extraFieldLines.AddRange([string[]][IO.File]::ReadAllLines($first))
    $extraFieldLines[1] += ',"unexpected"'
    [IO.File]::WriteAllLines($extraField, $extraFieldLines, [Text.UTF8Encoding]::new($true))
    Assert-Throws 'row has 13 fields; expected 12' {
        Import-LocalizationWorkbook -Repository $repository -Path $extraField -ValidateOnly
    } 'Extra data fields must fail closed instead of being silently discarded.'

    $invalidUtf8 = Join-Path $testRoot 'invalid-utf8.csv'
    $validUtf8Bytes = [IO.File]::ReadAllBytes($first)
    $invalidUtf8Bytes = [byte[]]::new($validUtf8Bytes.Length + 1)
    [Buffer]::BlockCopy($validUtf8Bytes, 0, $invalidUtf8Bytes, 0, $validUtf8Bytes.Length)
    $invalidUtf8Bytes[$invalidUtf8Bytes.Length - 1] = 0xC3
    [IO.File]::WriteAllBytes($invalidUtf8, $invalidUtf8Bytes)
    Assert-Throws 'not valid UTF-8 CSV' {
        Import-LocalizationWorkbook -Repository $repository -Path $invalidUtf8 -ValidateOnly
    } 'A BOM-prefixed workbook with malformed UTF-8 must fail during decoding.'

    $pseudoImport = Join-Path $testRoot 'pseudo-import.csv'
    $pseudoRows = @(Import-Csv -LiteralPath $first)
    foreach ($row in $pseudoRows) { $row.Locale = 'qps-ploc' }
    Write-CsvRows $pseudoRows $pseudoImport
    Assert-Throws 'cannot be used as a translator target' {
        Import-LocalizationWorkbook -Repository $repository -Path $pseudoImport -ValidateOnly
    } 'Pseudo-localization must not be importable.'

    $fixture = New-TranslatorFixture
    try {
        Import-Module (Join-Path $fixture 'build\LocalizationTranslatorWorkflow.psm1') -Force
        $fixtureWorkbook = Join-Path $testRoot 'fixture.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $fixture -Locale 'ru-RU' -Path $fixtureWorkbook)
        $before = @{}
        foreach ($path in @(Get-CatalogPaths $fixture)) {
            $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
        $roundTrip = Import-LocalizationWorkbook `
            -Repository $fixture -Path $fixtureWorkbook
        Assert-Equal 0 @($roundTrip.ChangedPaths).Count `
            'An unchanged export/import round trip must be a byte no-op.'
        foreach ($path in $before.Keys) {
            Assert-Equal $before[$path] `
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash `
                "Round trip changed catalog '$path'."
        }

        $staleTargetWorkbook = Join-Path $testRoot 'stale-target.csv'
        Copy-Item -LiteralPath $fixtureWorkbook -Destination $staleTargetWorkbook
        $staleTarget = Join-Path $fixture `
            'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw'
        $staleTargetBytes = [IO.File]::ReadAllBytes($staleTarget)
        $staleTargetText = [IO.File]::ReadAllText($staleTarget)
        $staleTargetText = [regex]::Replace(
            $staleTargetText,
            '(?s)(<data name="App_ExitDialog_Heading"[^>]*>.*?<value>).*?(</value>)',
            { param($match) $match.Groups[1].Value +
                'Newer parallel translation' + $match.Groups[2].Value })
        [IO.File]::WriteAllText(
            $staleTarget, $staleTargetText, [Text.UTF8Encoding]::new($false))
        $newerTargetHash = (Get-FileHash -LiteralPath $staleTarget -Algorithm SHA256).Hash
        Assert-Throws 'stale or tampered.*TargetCatalogHash' {
            Import-LocalizationWorkbook `
                -Repository $fixture -Path $staleTargetWorkbook
        } 'A newer target-catalog edit must stale the prior workbook before writing.'
        Assert-Equal $newerTargetHash `
            (Get-FileHash -LiteralPath $staleTarget -Algorithm SHA256).Hash `
            'Rejected stale-target import overwrote the newer catalog.'
        [IO.File]::WriteAllBytes($staleTarget, $staleTargetBytes)

        $fixtureRows = @(Import-Csv -LiteralPath $fixtureWorkbook)
        $changed = @{}
        foreach ($surface in @('tray', 'tray-core', 'installer', 'setup')) {
            $candidate = @($fixtureRows | Where-Object {
                    $_.Surface -ceq $surface -and
                    $_.Constraints -like 'Translate naturally*' -and
                    $_.Translation -notmatch '^[=+\-@]'
                })[0]
            Assert-True ($null -ne $candidate) "No safe fixture row found for $surface."
            $candidate.Translation += ' test'
            $changed[$surface] = $candidate.Key
        }
        $changedWorkbook = Join-Path $testRoot 'changed.csv'
        Write-CsvRows $fixtureRows $changedWorkbook
        $changedResult = Import-LocalizationWorkbook `
            -Repository $fixture -Path $changedWorkbook
        Assert-Equal 4 @($changedResult.ChangedPaths).Count `
            'One accepted translation on each surface must update four catalogs.'
        foreach ($path in @(Get-CatalogPaths $fixture)) {
            [xml]$document = [IO.File]::ReadAllText($path)
            Assert-True (@($document.root.data | Where-Object {
                        [string]$_.value -like '* test'
                    }).Count -eq 1) "Catalog '$path' did not receive exactly one update."
        }

        $staleWorkbook = Join-Path $testRoot 'stale.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $fixture -Locale 'ru-RU' -Path $staleWorkbook)
        $traySource = Join-Path $fixture `
            'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
        $sourceText = [IO.File]::ReadAllText($traySource)
        [IO.File]::WriteAllText(
            $traySource,
            $sourceText.Replace('Exit OpenBlade?', 'Exit OpenBlade now?'),
            [Text.UTF8Encoding]::new($false))
        Assert-Throws 'stale or tampered|missing' {
            Import-LocalizationWorkbook `
                -Repository $fixture -Path $staleWorkbook -ValidateOnly
        } 'A source change must stale the prior workbook.'

        $rollbackWorkbook = Join-Path $testRoot 'rollback-base.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $fixture -Locale 'ru-RU' -Path $rollbackWorkbook)
        $rollbackRows = @(Import-Csv -LiteralPath $rollbackWorkbook)
        $rollbackCandidate = @($rollbackRows | Where-Object {
                $_.Constraints -like 'Translate naturally*' -and
                $_.Translation -notlike '* test'
            })[0]
        $rollbackCandidate.Translation += ' rollback'
        Write-CsvRows $rollbackRows $rollbackWorkbook
        $rollbackHashes = @{}
        foreach ($path in @(Get-CatalogPaths $fixture)) {
            $rollbackHashes[$path] = (Get-FileHash $path -Algorithm SHA256).Hash
        }
        [IO.File]::WriteAllText(
            (Join-Path $fixture 'build\Validate-LocalizationResources.ps1'),
            "[CmdletBinding()]`r`nparam([string]`$Repository, [switch]`$AllowLockedCatalogRead)`r`nthrow 'fixture validation failed'`r`n",
            [Text.UTF8Encoding]::new($false))
        Assert-Throws 'fixture validation failed' {
            Import-LocalizationWorkbook -Repository $fixture -Path $rollbackWorkbook
        } 'A post-write validation failure must abort the import.'
        foreach ($path in $rollbackHashes.Keys) {
            Assert-Equal $rollbackHashes[$path] `
                (Get-FileHash $path -Algorithm SHA256).Hash `
                "Failed import did not restore '$path'."
        }
    }
    finally {
        Import-Module $modulePath -Force
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }


    $keysetFixture = New-TranslatorFixture
    try {
        Import-Module (Join-Path $keysetFixture `
            'build\LocalizationTranslatorWorkflow.psm1') -Force
        $keysetSource = Join-Path $keysetFixture `
            'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
        $keysetTarget = Join-Path $keysetFixture `
            'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw'
        $newSourceNode = @"
  <data name="Synthetic_New_Key.Text" xml:space="preserve">
    <value>Synthetic new label</value>
  </data>
"@
        $orphanTargetNode = @"
  <data name="Synthetic_Orphan_Key.Text" xml:space="preserve">
    <value>Obsolete translated label</value>
  </data>
"@
        [IO.File]::WriteAllText(
            $keysetSource,
            [IO.File]::ReadAllText($keysetSource).Replace(
                '</root>', $newSourceNode + "`r`n</root>"),
            [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(
            $keysetTarget,
            [IO.File]::ReadAllText($keysetTarget).Replace(
                '</root>', $orphanTargetNode + "`r`n</root>"),
            [Text.UTF8Encoding]::new($false))
        $keysetWorkbook = Join-Path $testRoot 'keyset.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $keysetFixture -Locale 'ru-RU' -Path $keysetWorkbook)
        $keysetRows = @(Import-Csv -LiteralPath $keysetWorkbook)
        @($keysetRows | Where-Object {
                $_.Surface -ceq 'tray' -and $_.Key -ceq 'Synthetic_New_Key.Text'
            })[0].Translation = 'Synthetic translated label'
        Write-CsvRows $keysetRows $keysetWorkbook
        [void](Import-LocalizationWorkbook `
            -Repository $keysetFixture -Path $keysetWorkbook)
        [xml]$keysetDocument = [IO.File]::ReadAllText($keysetTarget)
        $importedSynthetic = [string](@($keysetDocument.root.data | Where-Object {
                    [string]$_.name -ceq 'Synthetic_New_Key.Text'
                })[0].value)
        Assert-Equal 'Synthetic translated label' $importedSynthetic `
            'Import must add newly authoritative source keys to an existing locale.'
        Assert-Equal 0 @($keysetDocument.root.data | Where-Object {
                [string]$_.name -ceq 'Synthetic_Orphan_Key.Text'
            }).Count 'Import must remove keys absent from the authoritative source.'
    }
    finally {
        Import-Module $modulePath -Force
        Remove-Item -LiteralPath $keysetFixture -Recurse -Force
    }

    $sourceLockFixture = New-TranslatorFixture
    try {
        Import-Module (Join-Path $sourceLockFixture `
            'build\LocalizationTranslatorWorkflow.psm1') -Force
        $sourceLockWorkbook = Join-Path $testRoot 'source-lock.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $sourceLockFixture -Locale 'ru-RU' -Path $sourceLockWorkbook)
        $sourceLockRows = @(Import-Csv -LiteralPath $sourceLockWorkbook)
        $sourceLockCandidate = @($sourceLockRows | Where-Object {
                $_.Surface -ceq 'tray-core' -and
                $_.Constraints -like 'Translate naturally*'
            })[0]
        $sourceLockCandidate.Translation += ' lock test'
        Write-CsvRows $sourceLockRows $sourceLockWorkbook
        $sourceLockValidator = @'
[CmdletBinding()]
param([string]$Repository, [switch]$AllowLockedCatalogRead)
$source = Join-Path $Repository 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
$writeHandle = $null
try {
    $writeHandle = [IO.File]::Open(
        $source,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
}
catch [IO.IOException] {
    return
}
finally {
    if ($null -ne $writeHandle) { $writeHandle.Dispose() }
}
throw 'Authoritative source catalog was not read-locked during import.'
'@
        [IO.File]::WriteAllText(
            (Join-Path $sourceLockFixture 'build\Validate-LocalizationResources.ps1'),
            $sourceLockValidator,
            [Text.UTF8Encoding]::new($false))
        $sourceLockResult = Import-LocalizationWorkbook `
            -Repository $sourceLockFixture -Path $sourceLockWorkbook
        Assert-Equal 1 @($sourceLockResult.ChangedPaths).Count `
            'The source-lock fixture must exercise a real catalog write.'
        $sourcePath = Join-Path $sourceLockFixture `
            'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
        $releasedHandle = [IO.File]::Open(
            $sourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        $releasedHandle.Dispose()
    }
    finally {
        Import-Module $modulePath -Force
        Remove-Item -LiteralPath $sourceLockFixture -Recurse -Force
    }

    $targetLockFixture = New-TranslatorFixture
    try {
        Import-Module (Join-Path $targetLockFixture `
            'build\LocalizationTranslatorWorkflow.psm1') -Force
        $targetLockWorkbook = Join-Path $testRoot 'target-lock.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $targetLockFixture -Locale 'ru-RU' -Path $targetLockWorkbook)
        $targetLockRows = @(Import-Csv -LiteralPath $targetLockWorkbook)
        $targetLockCandidate = @($targetLockRows | Where-Object {
                $_.Surface -ceq 'tray-core' -and
                $_.Constraints -like 'Translate naturally*'
            })[0]
        $targetLockCandidate.Translation += ' target lock test'
        Write-CsvRows $targetLockRows $targetLockWorkbook
        $targetLockValidator = @'
[CmdletBinding()]
param([string]$Repository, [switch]$AllowLockedCatalogRead)
$target = Join-Path $Repository 'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw'
$writeHandle = $null
try {
    $writeHandle = [IO.File]::Open(
        $target,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
}
catch [IO.IOException] {
    return
}
finally {
    if ($null -ne $writeHandle) { $writeHandle.Dispose() }
}
throw 'Target catalog was not write-locked during import.'
'@
        [IO.File]::WriteAllText(
            (Join-Path $targetLockFixture 'build\Validate-LocalizationResources.ps1'),
            $targetLockValidator,
            [Text.UTF8Encoding]::new($false))
        $targetLockResult = Import-LocalizationWorkbook `
            -Repository $targetLockFixture -Path $targetLockWorkbook
        Assert-Equal 1 @($targetLockResult.ChangedPaths).Count `
            'The target-lock fixture must exercise a real catalog write.'
        $targetPath = Join-Path $targetLockFixture `
            'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw'
        $releasedTargetHandle = [IO.File]::Open(
            $targetPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        $releasedTargetHandle.Dispose()
    }
    finally {
        Import-Module $modulePath -Force
        Remove-Item -LiteralPath $targetLockFixture -Recurse -Force
    }

    $markupFixture = New-TranslatorFixture
    try {
        Import-Module (Join-Path $markupFixture `
            'build\LocalizationTranslatorWorkflow.psm1') -Force
        $markupSource = Join-Path $markupFixture `
            'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
        $markupTranslation = Join-Path $markupFixture `
            'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw'
        $sourceText = [IO.File]::ReadAllText($markupSource).Replace(
            '<value>Exit OpenBlade?</value>',
            '<value>Exit &lt;b&gt;OpenBlade.exe&lt;/b&gt;?</value>')
        $translationText = [regex]::Replace(
            [IO.File]::ReadAllText($markupTranslation),
            '(?s)(<data name="App_ExitDialog_Heading"[^>]*>.*?<value>).*?(</value>)',
            { param($match) $match.Groups[1].Value +
                'Close &lt;b&gt;OpenBlade.exe&lt;/b&gt;?' +
                $match.Groups[2].Value })
        [IO.File]::WriteAllText(
            $markupSource, $sourceText, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(
            $markupTranslation, $translationText, [Text.UTF8Encoding]::new($false))
        $markupWorkbook = Join-Path $testRoot 'markup.csv'
        [void](Export-LocalizationWorkbook `
            -Repository $markupFixture -Locale 'ru-RU' -Path $markupWorkbook)
        $markupRows = @(Import-Csv $markupWorkbook)
        $markupRow = @($markupRows | Where-Object Key -ceq 'App_ExitDialog_Heading')[0]
        $markupRow.Translation = $markupRow.Translation.Replace('<b>', '').Replace('</b>', '')
        Write-CsvRows $markupRows $markupWorkbook
        Assert-Throws 'markup structure' {
            Import-LocalizationWorkbook `
                -Repository $markupFixture -Path $markupWorkbook -ValidateOnly
        } 'Markup removal must fail closed.'

        [void](Export-LocalizationWorkbook `
            -Repository $markupFixture -Locale 'ru-RU' -Path $markupWorkbook)
        $invariantRows = @(Import-Csv $markupWorkbook)
        $invariantRow = @($invariantRows |
            Where-Object Key -ceq 'App_ExitDialog_Heading')[0]
        $invariantRow.Translation = $invariantRow.Translation.Replace(
            'OpenBlade.exe', 'OpenBlade.bin')
        Write-CsvRows $invariantRows $markupWorkbook
        Assert-Throws 'protected token' {
            Import-LocalizationWorkbook `
                -Repository $markupFixture -Path $markupWorkbook -ValidateOnly
        } 'Protected technical identifiers must remain exact.'

        [void](Export-LocalizationWorkbook `
            -Repository $markupFixture -Locale 'ru-RU' -Path $markupWorkbook)
        $injectedTokenRows = @(Import-Csv $markupWorkbook)
        $injectedTokenRow = @($injectedTokenRows |
            Where-Object Constraints -like 'Translate naturally*')[0]
        $injectedTokenRow.Translation += ' https://example.invalid/payload.exe'
        Write-CsvRows $injectedTokenRows $markupWorkbook
        Assert-Throws 'protected token' {
            Import-LocalizationWorkbook `
                -Repository $markupFixture -Path $markupWorkbook -ValidateOnly
        } 'Translations must not inject protected technical identifiers.'
    }
    finally {
        Import-Module $modulePath -Force
        Remove-Item -LiteralPath $markupFixture -Recurse -Force
    }

    $exportScript = [IO.File]::ReadAllText((
        Join-Path $repository 'build\Export-LocalizationTranslatorPackage.ps1'))
    Assert-True ($exportScript.Contains("-Culture 'en-US'")) `
        'Translator packages must generate source-language context.'
    Assert-True ($exportScript -notmatch '(?i)RunAs|Publish\.ps1|InstallerValidation|Start-Service') `
        'Translator packaging must not install, elevate, publish, or start services.'

    $packageScript = Join-Path $repository 'build\Export-LocalizationTranslatorPackage.ps1'
    $packageRoot = Join-Path $testRoot 'packages'
    $protectedPackage = Join-Path $packageRoot 'OpenBlade-qps-ploc-translator'
    [IO.Directory]::CreateDirectory($protectedPackage) | Out-Null
    $sentinel = Join-Path $protectedPackage 'sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'preserve')
    Assert-Throws 'cannot be used as a translator target' {
        & $packageScript -Locale qps-ploc -OutputDirectory $packageRoot -Force
    } 'Locale validation must happen before replacing an existing package.'
    Assert-True (Test-Path -LiteralPath $sentinel -PathType Leaf) `
        'A rejected locale must not delete an existing package path.'

    & $packageScript -Locale ru-RU -OutputDirectory $packageRoot -Force
    $archive = Join-Path $packageRoot 'OpenBlade-ru-RU-translator.zip'
    $referenceArchive = Join-Path $testRoot 'reference-translator.zip'
    Copy-Item -LiteralPath $archive -Destination $referenceArchive
    $archiveEvidenceRoot = Join-Path $repository 'artifacts\translator-archive-failures'
    $generatedFolder = Join-Path $packageRoot 'OpenBlade-ru-RU-translator'
    $manifest = Get-Content (Join-Path $generatedFolder 'manifest.json') -Raw |
        ConvertFrom-Json
    Assert-Equal 'UTC-for-snapshot-timestamps' $manifest.screenshotRenderProfile.timeZone `
        'Snapshot timestamps must not depend on the machine timezone.'
    Assert-Equal 'bgra8-4-bit-midpoint-local-majority-edge-alpha-filled-corner-v4' `
        $manifest.screenshotRenderProfile.pixelCanonicalization `
        'The package manifest must record deterministic pixel canonicalization.'
    Assert-Equal 'two-consecutive-canonical-frames-v1' `
        $manifest.screenshotRenderProfile.frameStabilization `
        'The package manifest must record deterministic frame stabilization.'
    Assert-Equal 'stable-title-foreground-v1' `
        $manifest.screenshotRenderProfile.windowActivation `
        'The package manifest must record deterministic title activation styling.'
    $generatedWorkbook = Join-Path $generatedFolder 'OpenBlade-ru-RU.csv'
    Assert-Equal ((Get-FileHash -LiteralPath $generatedWorkbook -Algorithm SHA256).
        Hash.ToLowerInvariant()) $manifest.workbookSha256 `
        'The package manifest must bind the exact workbook bytes.'
    $generatedRows = @(Import-Csv -LiteralPath $generatedWorkbook)
    Assert-Equal $generatedRows[0].TargetCatalogHash $manifest.targetCatalogHash `
        'The package manifest must expose the workbook target-catalog binding.'
    Assert-Equal 'noninteractive-window-v1' $manifest.screenshotRenderProfile.inputIsolation `
        'Snapshot windows must reject interaction while capturing.'
    & $packageScript -Locale ru-RU -OutputDirectory $packageRoot -Force
    Assert-ArchiveEqual -Reference $referenceArchive -Actual $archive `
        -Stage 'same-process' -EvidenceRoot $archiveEvidenceRoot `
        -Message 'Repeated package exports must produce the same archive bytes.'
    $freshPowerShell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') {
        'pwsh.exe'
    } else {
        'powershell.exe'
    })
    & $freshPowerShell -NoProfile -ExecutionPolicy Bypass `
        -File $packageScript `
        -Locale ru-RU `
        -OutputDirectory $packageRoot `
        -Force
    if ($LASTEXITCODE -ne 0) {
        throw 'Fresh-process translator package export failed.'
    }
    Assert-ArchiveEqual -Reference $referenceArchive -Actual $archive `
        -Stage 'fresh-process' -EvidenceRoot $archiveEvidenceRoot `
        -Message 'Fresh-process package exports must produce the same archive bytes.'

    $captureScript = [IO.File]::ReadAllText((
        Join-Path $repository 'build\Capture-LocalizationSnapshots.ps1'))
    foreach ($volatileField in @(
            'generatedAtUtc', 'workingTreeDiff', 'operatingSystem', 'appliedDpi')) {
        Assert-True (-not $captureScript.Contains($volatileField)) `
            "Snapshot manifest must not expose volatile field '$volatileField'."
    }
    Assert-True ($captureScript.Contains("`$env:__COMPAT_LAYER = 'DPIUNAWARE'")) `
        'Snapshot children must run in the process-local 96-DPI compatibility context.'
    Assert-True ($captureScript.Contains(
            '$env:__COMPAT_LAYER = $previousCompatibilityLayer')) `
        'Snapshot capture must restore the caller compatibility environment.'
    Assert-True ($captureScript.Contains(
            'artifacts\translator-archive-failures\snapshot-stability\$Culture')) `
        'Snapshot timeouts must retain evidence outside disposable package fixtures.'
    Assert-True ($captureScript.Contains("[Guid]::NewGuid().ToString('N')")) `
        'Each snapshot capture invocation must isolate its failure evidence.'
    Assert-True ($captureScript.Contains(
            '$env:OPENBLADE_LOCALIZATION_FAILURE_EVIDENCE_DIR = $failureEvidenceRoot')) `
        'Both disconnected renderers must receive the durable failure-evidence root.'
    Assert-True ($captureScript.Contains(
            '$env:OPENBLADE_LOCALIZATION_FAILURE_EVIDENCE_DIR = $previousFailureEvidenceDirectory')) `
        'Snapshot capture must restore the caller failure-evidence environment.'
}
finally {
    Import-Module $modulePath -Force
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected translator test cleanup target: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Write-Host 'Localization translator workflow tests passed.'
