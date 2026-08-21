[CmdletBinding()]
param()

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
        "[CmdletBinding()]`r`nparam([string]`$Repository)`r`n",
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
    $first = Join-Path $testRoot 'first.csv'
    $second = Join-Path $testRoot 'second.csv'
    $model = Export-LocalizationWorkbook -Repository $repository -Locale 'ru-RU' -Path $first
    [void](Export-LocalizationWorkbook -Repository $repository -Locale 'ru-RU' -Path $second)
    $expectedRowCount = Get-AuthoritativeSourceRowCount
    Assert-Equal $expectedRowCount @($model.Rows).Count `
        'The workbook must cover every row in all four catalogs.'
    Assert-Equal 4 @($model.Rows | Group-Object Surface).Count `
        'The workbook must contain four distinct surfaces.'
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
    Assert-Throws 'row has 12 fields; expected 11' {
        Import-LocalizationWorkbook -Repository $repository -Path $extraField -ValidateOnly
    } 'Extra data fields must fail closed instead of being silently discarded.'

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
        Copy-Item -LiteralPath $fixtureWorkbook -Destination $staleWorkbook
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
            "[CmdletBinding()]`r`nparam([string]`$Repository)`r`nthrow 'fixture validation failed'`r`n",
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
        & $packageScript -Locale qps-ploc -OutputDirectory $packageRoot `
            -SkipScreenshots -Force
    } 'Locale validation must happen before replacing an existing package.'
    Assert-True (Test-Path -LiteralPath $sentinel -PathType Leaf) `
        'A rejected locale must not delete an existing package path.'

    & $packageScript -Locale ru-RU -OutputDirectory $packageRoot `
        -SkipScreenshots -Force
    $archive = Join-Path $packageRoot 'OpenBlade-ru-RU-translator.zip'
    $firstArchiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $generatedFolder = Join-Path $packageRoot 'OpenBlade-ru-RU-translator'
    $manifest = Get-Content (Join-Path $generatedFolder 'manifest.json') -Raw |
        ConvertFrom-Json
    $generatedWorkbook = Join-Path $generatedFolder 'OpenBlade-ru-RU.csv'
    Assert-Equal ((Get-FileHash -LiteralPath $generatedWorkbook -Algorithm SHA256).
        Hash.ToLowerInvariant()) $manifest.workbookSha256 `
        'The package manifest must bind the exact workbook bytes.'
    & $packageScript -Locale ru-RU -OutputDirectory $packageRoot `
        -SkipScreenshots -Force
    Assert-Equal $firstArchiveHash `
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash `
        'Repeated package exports must produce the same archive bytes.'

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
