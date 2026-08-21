Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:WorkbookFormatVersion = '2'
$script:WorkbookHeaders = @(
    'FormatVersion',
    'Locale',
    'Surface',
    'Key',
    'Source',
    'Translation',
    'Context',
    'Constraints',
    'Screenshots',
    'SourceHash',
    'CatalogHash',
    'TargetCatalogHash')

$script:AccessKeyGroups = @(
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'fresh-install actions'
        Keys = @('_Install', '_Cancel')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'maintenance actions'
        Keys = @(
            '_Repair',
            '_Uninstall',
            'Remove saved _profiles and settings',
            '_Cancel')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'newer-version actions'
        Keys = @('_Overwrite', '_Cancel')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'collapsed progress actions'
        Keys = @('Show _details', '_Cancel')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'expanded progress actions'
        Keys = @('Hide _details', '_Cancel')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'success actions'
        Keys = @('_Launch OpenBlade', '_Close')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'failure actions'
        Keys = @('_Open log', '_Close')
    },
    [pscustomobject]@{
        Surface = 'installer'
        Context = 'cancel-confirmation actions'
        Keys = @('Keep _working', 'Cancel _setup')
    })

function Get-LocalizationRepository {
    param([string]$Repository)

    if ([string]::IsNullOrWhiteSpace($Repository)) {
        $Repository = Split-Path -Parent $PSScriptRoot
    }
    return [IO.Path]::GetFullPath($Repository)
}

function Get-LocalizationCatalogDefinitions {
    param([Parameter(Mandatory)][string]$Repository)

    return @(
        [pscustomobject]@{
            Surface = 'tray'
            SourcePath = 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
            TranslationPath = {
                param($locale)
                "src\OpenBlade.Tray\Strings\$locale\Resources.resw"
            }
        },
        [pscustomobject]@{
            Surface = 'tray-core'
            SourcePath = 'src\OpenBlade.Tray.Core\Properties\Resources.resx'
            TranslationPath = {
                param($locale)
                "src\OpenBlade.Tray.Core\Properties\Resources.$locale.resx"
            }
        },
        [pscustomobject]@{
            Surface = 'installer'
            SourcePath = 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx'
            TranslationPath = {
                param($locale)
                "build\installer\OpenBlade.Installer.UI\Properties\Resources.$locale.resx"
            }
        },
        [pscustomobject]@{
            Surface = 'setup'
            SourcePath = 'src\OpenBlade.Setup\Properties\Resources.resx'
            TranslationPath = {
                param($locale)
                "src\OpenBlade.Setup\Properties\Resources.$locale.resx"
            }
        })
}

function Assert-TranslatorLocale {
    param([Parameter(Mandatory)][string]$Locale)

    if ([string]::Equals($Locale, 'en-US', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Locale, 'qps-ploc', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Locale '$Locale' cannot be used as a translator target."
    }
    try {
        $culture = [Globalization.CultureInfo]::GetCultureInfo($Locale)
    }
    catch {
        throw "Translator locale '$Locale' is not a recognized .NET culture."
    }
    if ($culture.IsNeutralCulture -or $culture.Name -cne $Locale) {
        throw "Translator locale '$Locale' must be a canonical, specific culture name."
    }
}

function Read-LocalizationCatalog {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    try {
        [xml]$document = [IO.File]::ReadAllText(
            $Path,
            [Text.UTF8Encoding]::new($false, $true))
    }
    catch {
        throw "Localization catalog is malformed: $Path. $($_.Exception.Message)"
    }
    $values = [Collections.Generic.Dictionary[string,string]]::new(
        [StringComparer]::Ordinal)
    foreach ($entry in @($document.root.data)) {
        $key = [string]$entry.name
        if ([string]::IsNullOrWhiteSpace($key) -or $values.ContainsKey($key)) {
            throw "Localization catalog has a blank or duplicate key: $Path"
        }
        $values.Add($key, [string]$entry.value)
    }
    return [pscustomobject]@{ Document = $document; Values = $values }
}

function Get-LengthPrefixedText {
    param([AllowEmptyString()][string]$Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return $bytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + ':' + $Value
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Value)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try {
            return [BitConverter]::ToString($algorithm.ComputeHash($stream)).
                Replace('-', '').ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-LocalizationTargetCatalogHash {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Locale)

    $state = ''
    foreach ($definition in @(Get-LocalizationCatalogDefinitions $Repository)) {
        $relative = (& $definition.TranslationPath $Locale).Replace('\', '/')
        $path = Join-Path $Repository $relative
        $state += Get-LengthPrefixedText ([string]$definition.Surface)
        $state += Get-LengthPrefixedText $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $state += Get-LengthPrefixedText 'present'
            $state += Get-LengthPrefixedText (Get-FileSha256 $path)
        }
        else {
            $state += Get-LengthPrefixedText 'missing'
        }
    }
    return Get-TextSha256 $state
}

function Sort-OrdinalStrings {
    param([AllowEmptyCollection()][object[]]$Values)

    $ordered = [Collections.Generic.List[string]]::new()
    foreach ($value in $Values) { $ordered.Add([string]$value) }
    $ordered.Sort([StringComparer]::Ordinal)
    return $ordered.ToArray()
}

function Test-OrdinalSequenceEqual {
    param(
        [AllowEmptyCollection()][object[]]$Left,
        [AllowEmptyCollection()][object[]]$Right)

    if ($Left.Count -ne $Right.Count) { return $false }
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if (-not [StringComparer]::Ordinal.Equals(
                [string]$Left[$index],
                [string]$Right[$index])) {
            return $false
        }
    }
    return $true
}

function Get-CompositeFormatItems {
    param([AllowEmptyString()][string]$Value)

    $pattern = '(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]*)?\}(?!\})'
    return @(Sort-OrdinalStrings @(
            [regex]::Matches($Value, $pattern) | ForEach-Object Value))
}

function Assert-ValidCompositeFormat {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][string]$Description)

    $indices = [Collections.Generic.List[int]]::new()
    foreach ($match in @([regex]::Matches($Value, '(?<!\{)\{(?<index>\d+)'))) {
        $index = 0
        if (-not [int]::TryParse($match.Groups['index'].Value, [ref]$index) -or
            $index -gt 1024) {
            throw "$Description contains an invalid .NET format string."
        }
        $indices.Add($index)
    }
    $argumentCount = if ($indices.Count -eq 0) { 0 }
        else { 1 + ($indices | Measure-Object -Maximum).Maximum }
    [object[]]$arguments = @('value') * $argumentCount
    try {
        [void][string]::Format(
            [Globalization.CultureInfo]::InvariantCulture,
            $Value,
            $arguments)
    }
    catch [FormatException] {
        throw "$Description contains an invalid .NET format string."
    }
}

function Get-AccessKeys {
    param([AllowEmptyString()][string]$Value)

    return @(Sort-OrdinalStrings @(
            [regex]::Matches($Value, '(?<!_)_(?!_)[\p{L}\p{Nd}]') |
                ForEach-Object Value))
}

function Get-MarkupTokens {
    param([AllowEmptyString()][string]$Value)

    return @([regex]::Matches($Value, '</?[A-Za-z][^<>]*>') |
        ForEach-Object Value)
}

function Get-InvariantTokens {
    param([AllowEmptyString()][string]$Value)

    $pattern = '(?:https?://[^\s<]+|(?<![\p{L}\p{Nd}_])(?:Ctrl|Alt|Shift|Win)(?:\+(?:Ctrl|Alt|Shift|Win|F(?:[1-9]|1[0-2])|[A-Z0-9]))+|\b[A-Za-z]:\\(?:[^\\/:*?"<>|\r\n]+\\)*[^\\/:*?"<>|\r\n]+\.(?:exe|dll|json|oblight|chroma)\b|\bM[1-5]\b|\bRZ09-\d{4}\b|\b1532:[0-9A-Fa-f]{4}\b|\b0x[0-9A-Fa-f]+\b|\b[\w.-]+\.(?:exe|dll|json|oblight|chroma)\b)'
    return @(Sort-OrdinalStrings @(
            [regex]::Matches($Value, $pattern) | ForEach-Object Value))
}

function Get-EdgeWhitespace {
    param([AllowEmptyString()][string]$Value)

    $leading = [regex]::Match($Value, '^\s+').Value
    $trailing = [regex]::Match($Value, '\s+$').Value
    return @($leading.Replace("`r`n", "`n"), $trailing.Replace("`r`n", "`n"))
}

function Assert-SafeCellText {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][string]$Description)

    if ([regex]::IsMatch(
            $Value,
            '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F\u2028\u2029]') -or
        [regex]::IsMatch($Value, '\r(?!\n)')) {
        throw "$Description contains a forbidden control character or line separator."
    }
    if ($Value.Contains("`t")) {
        throw "$Description contains a tab; tabs are reserved for spreadsheet formula escaping."
    }
}

function Assert-TranslationValue {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Translation,
        [Parameter(Mandatory)][string]$Description)

    if ([string]::IsNullOrWhiteSpace($Translation)) {
        throw "$Description is blank."
    }
    Assert-SafeCellText $Translation $Description
    Assert-ValidCompositeFormat $Translation $Description

    $sourceItems = @(Get-CompositeFormatItems $Source)
    $translationItems = @(Get-CompositeFormatItems $Translation)
    if (-not (Test-OrdinalSequenceEqual $sourceItems $translationItems)) {
        throw "$Description changed its .NET format placeholders."
    }
    if (-not (Test-OrdinalSequenceEqual `
                @(Get-MarkupTokens $Source) `
                @(Get-MarkupTokens $Translation))) {
        throw "$Description changed its markup structure."
    }
    if (@(Get-AccessKeys $Source).Count -ne @(Get-AccessKeys $Translation).Count) {
        throw "$Description changed its access-key marker count."
    }
    $sourceEdges = @(Get-EdgeWhitespace $Source)
    $translationEdges = @(Get-EdgeWhitespace $Translation)
    if ($sourceEdges[0] -cne $translationEdges[0] -or
        $sourceEdges[1] -cne $translationEdges[1]) {
        throw "$Description changed significant leading or trailing whitespace."
    }
    $sourceNewlines = ([regex]::Matches($Source.Replace("`r`n", "`n"), "`n")).Count
    $translationNewlines = ([regex]::Matches(
            $Translation.Replace("`r`n", "`n"), "`n")).Count
    if ($sourceNewlines -ne $translationNewlines) {
        throw "$Description changed its explicit line-break count."
    }
    if ($Source -notmatch '\p{L}' -and $Translation -cne $Source) {
        throw "$Description changed punctuation-only invariant text."
    }
    $sourceInvariants = @(Get-InvariantTokens $Source)
    $translationInvariants = @(Get-InvariantTokens $Translation)
    foreach ($group in @($sourceInvariants | Group-Object -CaseSensitive)) {
        $actualCount = @($translationInvariants | Where-Object { $_ -ceq $group.Name }).Count
        if ($actualCount -ne $group.Count) {
            throw "$Description changed protected token '$($group.Name)'."
        }
    }
}

function Assert-AccessKeyGroups {
    param([Parameter(Mandatory)]$RowsByIdentity)

    foreach ($group in $script:AccessKeyGroups) {
        $seen = [Collections.Generic.Dictionary[string,string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($key in @($group.Keys)) {
            $identity = [string]$group.Surface + "`0" + [string]$key
            if (-not $RowsByIdentity.ContainsKey($identity)) {
                continue
            }
            $row = $RowsByIdentity[$identity]
            foreach ($marker in @(Get-AccessKeys ([string]$row.Translation))) {
                $accessKey = $marker.Substring(1)
                if ($seen.ContainsKey($accessKey)) {
                    throw "Translation access key '$accessKey' collides in $($group.Context): '$($seen[$accessKey])' and '$($row.Surface)/$($row.Key)'."
                }
                $seen.Add($accessKey, "$($row.Surface)/$($row.Key)")
            }
        }
    }
}

function Get-TranslatorConstraints {
    param([Parameter(Mandatory)][string]$Source)

    $constraints = [Collections.Generic.List[string]]::new()
    $items = @(Get-CompositeFormatItems $Source)
    if ($items.Count -gt 0) {
        $constraints.Add('Keep format items: ' + ($items -join ' '))
    }
    $markup = @(Get-MarkupTokens $Source)
    if ($markup.Count -gt 0) {
        $constraints.Add('Keep markup structure: ' + ($markup -join ' '))
    }
    $accessKeyCount = @(Get-AccessKeys $Source).Count
    if ($accessKeyCount -gt 0) {
        $constraints.Add("Keep $accessKeyCount underscore access-key marker(s)")
    }
    $edges = @(Get-EdgeWhitespace $Source)
    if ($edges[0].Length -gt 0 -or $edges[1].Length -gt 0) {
        $constraints.Add('Keep leading and trailing whitespace exactly')
    }
    $newlines = ([regex]::Matches($Source.Replace("`r`n", "`n"), "`n")).Count
    if ($newlines -gt 0) {
        $constraints.Add("Keep $newlines explicit line break(s)")
    }
    $invariants = @(Get-InvariantTokens $Source)
    if ($invariants.Count -gt 0) {
        $constraints.Add('Keep exact tokens: ' + ($invariants -join ' '))
    }
    if ($Source -notmatch '\p{L}') {
        $constraints.Add('Copy exactly; punctuation-only invariant')
    }
    if ($constraints.Count -eq 0) {
        $constraints.Add('Translate naturally; do not add leading or trailing whitespace')
    }
    return $constraints -join '; '
}

function Read-LocalizationScreenshotMap {
    param([Parameter(Mandatory)][string]$Repository)

    $path = Join-Path $Repository 'build\localization\TranslatorScreenshotMap.json'
    try {
        $map = [IO.File]::ReadAllText(
            $path,
            [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    }
    catch {
        throw "Translator screenshot map is missing or malformed: $path. $($_.Exception.Message)"
    }
    if ($map.schemaVersion -ne 1 -or @($map.screenshots).Count -eq 0) {
        throw 'Translator screenshot map has an unsupported schema or no screenshots.'
    }
    $knownScreenshots = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($relative in @($map.screenshots | ForEach-Object { [string]$_ })) {
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or
            @($relative.Split('/')) -contains '..' -or
            $relative -notmatch '^(?:tray|installer)/[^/]+\.png$' -or
            -not $knownScreenshots.Add($relative)) {
            throw "Translator screenshot map has an unsafe or duplicate screenshot '$relative'."
        }
    }
    foreach ($rule in @($map.rules)) {
        if (@('tray', 'tray-core', 'installer', 'setup') -cnotcontains [string]$rule.surface -or
            @('key', 'source') -cnotcontains [string]$rule.match -or
            [string]::IsNullOrWhiteSpace([string]$rule.context) -or
            @($rule.screenshots).Count -eq 0) {
            throw 'Translator screenshot map has an incomplete rule.'
        }
        try { [void][regex]::new([string]$rule.pattern) }
        catch { throw "Translator screenshot map has an invalid pattern '$($rule.pattern)'." }
        foreach ($relative in @($rule.screenshots | ForEach-Object { [string]$_ })) {
            if (-not $knownScreenshots.Contains($relative)) {
                throw "Translator screenshot rule references unknown image '$relative'."
            }
        }
    }
    return $map
}

function Get-LocalizationContext {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Surface,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Source)

    foreach ($rule in @($Map.rules)) {
        if ([string]$rule.surface -cne $Surface) { continue }
        $matchText = if ([string]$rule.match -ceq 'source') { $Source } else { $Key }
        if (-not [regex]::IsMatch(
                $matchText,
                [string]$rule.pattern,
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            continue
        }
        $screenshots = @($rule.screenshots | ForEach-Object { 'context/' + [string]$_ })
        if ($screenshots.Count -eq 0) {
            throw "Screenshot rule for '$Surface/$Key' has no screenshot."
        }
        return [pscustomobject]@{
            Context = [string]$rule.context
            Screenshots = $screenshots -join ';'
        }
    }
    throw "No translator screenshot rule matches '$Surface/$Key'."
}

function Get-LocalizationWorkbookModel {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Locale,
        [switch]$ExcludeTranslations)

    Assert-TranslatorLocale $Locale
    $map = Read-LocalizationScreenshotMap $Repository
    $targetCatalogHash = Get-LocalizationTargetCatalogHash $Repository $Locale
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($definition in @(Get-LocalizationCatalogDefinitions $Repository)) {
        $sourcePath = Join-Path $Repository $definition.SourcePath
        $sourceCatalog = Read-LocalizationCatalog $sourcePath
        if ($null -eq $sourceCatalog) {
            throw "Authoritative localization catalog is missing: $sourcePath"
        }
        $relativeTranslation = & $definition.TranslationPath $Locale
        $translationCatalog = if ($ExcludeTranslations) { $null }
            else { Read-LocalizationCatalog (Join-Path $Repository $relativeTranslation) }
        foreach ($key in @(Sort-OrdinalStrings @($sourceCatalog.Values.Keys))) {
            $source = [string]$sourceCatalog.Values[$key]
            Assert-SafeCellText $source "Source resource '$($definition.Surface)/$key'"
            Assert-ValidCompositeFormat $source "Source resource '$($definition.Surface)/$key'"
            $context = Get-LocalizationContext $map $definition.Surface $key $source
            $translation = ''
            if ($null -ne $translationCatalog -and $translationCatalog.Values.ContainsKey($key)) {
                $translation = [string]$translationCatalog.Values[$key]
            }
            $row = [ordered]@{
                FormatVersion = $script:WorkbookFormatVersion
                Locale = $Locale
                Surface = $definition.Surface
                Key = $key
                Source = $source
                Translation = $translation
                Context = $context.Context
                Constraints = Get-TranslatorConstraints $source
                Screenshots = $context.Screenshots
                SourceHash = ''
                CatalogHash = ''
                TargetCatalogHash = $targetCatalogHash
            }
            $hashText = ''
            foreach ($field in @('FormatVersion', 'Locale', 'Surface', 'Key', 'Source',
                    'Context', 'Constraints', 'Screenshots')) {
                $hashText += Get-LengthPrefixedText ([string]$row[$field])
            }
            $row.SourceHash = Get-TextSha256 $hashText
            $rows.Add([pscustomobject]$row)
        }
    }
    $rows.Sort([Comparison[object]]{
            param($left, $right)
            $surfaceOrder = [StringComparer]::Ordinal.Compare(
                [string]$left.Surface,
                [string]$right.Surface)
            if ($surfaceOrder -ne 0) { return $surfaceOrder }
            return [StringComparer]::Ordinal.Compare(
                [string]$left.Key,
                [string]$right.Key)
        })
    $orderedRows = @($rows)
    $catalogText = ''
    foreach ($row in $orderedRows) {
        $catalogText += Get-LengthPrefixedText ([string]$row.SourceHash)
    }
    $catalogHash = Get-TextSha256 $catalogText
    foreach ($row in $orderedRows) { $row.CatalogHash = $catalogHash }
    return [pscustomobject]@{
        Rows = $orderedRows
        CatalogHash = $catalogHash
        TargetCatalogHash = $targetCatalogHash
        ScreenshotMap = $map
    }
}

function ConvertTo-SpreadsheetCell {
    param([AllowEmptyString()][string]$Value)

    if ($Value -match '^\s*[=+\-@]') { return "`t$Value" }
    return $Value
}

function ConvertFrom-SpreadsheetCell {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][string]$Description)

    if ($Value -match "^`t\s*[=+\-@]") { return $Value.Substring(1) }
    if ($Value -match '^\s*[=+\-@]') {
        throw "$Description begins with a spreadsheet formula character. Prefix it with one tab to mark literal text."
    }
    return $Value
}

function Export-LocalizationWorkbook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Locale,
        [Parameter(Mandatory)][string]$Path,
        [string]$Repository)

    $Repository = Get-LocalizationRepository $Repository
    $model = Get-LocalizationWorkbookModel $Repository $Locale
    $transportRows = foreach ($row in $model.Rows) {
        $copy = [ordered]@{}
        foreach ($header in $script:WorkbookHeaders) {
            $copy[$header] = ConvertTo-SpreadsheetCell ([string]$row.$header)
        }
        [pscustomobject]$copy
    }
    $csvLines = @($transportRows | ConvertTo-Csv -NoTypeInformation)
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllLines(
        [IO.Path]::GetFullPath($Path),
        $csvLines,
        [Text.UTF8Encoding]::new($true))
    return $model
}

function Read-TranslatorWorkbookRows {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Translator workbook does not exist: $Path"
    }
    $parser = $null
    $textReader = $null
    try {
        $text = [IO.File]::ReadAllText(
            $Path,
            [Text.UTF8Encoding]::new($false, $true))
        Add-Type -AssemblyName Microsoft.VisualBasic
        $textReader = [IO.StringReader]::new($text)
        $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($textReader)
        $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
        $parser.SetDelimiters(',')
        $parser.HasFieldsEnclosedInQuotes = $true
        $parser.TrimWhiteSpace = $false
        $headers = @($parser.ReadFields())
        if (-not (Test-OrdinalSequenceEqual $script:WorkbookHeaders $headers)) {
            throw "Translator workbook headers must be exactly: $($script:WorkbookHeaders -join ', ')."
        }
        while (-not $parser.EndOfData) {
            $fields = @($parser.ReadFields())
            if ($fields.Count -ne $script:WorkbookHeaders.Count) {
                throw "Translator workbook row has $($fields.Count) fields; expected $($script:WorkbookHeaders.Count)."
            }
        }
        # TextFieldParser supplies strict structural validation, but normalizes
        # embedded LF characters to CRLF. ConvertFrom-Csv preserves the exact
        # line endings used by resource keys and values, so use it only after
        # every record has proved to contain exactly the expected field count.
        $rows = @($text | ConvertFrom-Csv)
    }
    catch {
        throw "Translator workbook is not valid UTF-8 CSV: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $parser) { $parser.Dispose() }
        if ($null -ne $textReader) { $textReader.Dispose() }
    }
    if ($rows.Count -eq 0) { throw 'Translator workbook contains no data rows.' }
    foreach ($row in $rows) {
        foreach ($header in $script:WorkbookHeaders) {
            $row.$header = ConvertFrom-SpreadsheetCell ([string]$row.$header) "Workbook cell '$header'"
        }
    }
    return $rows
}

function ConvertTo-ResourceXmlValue {
    param(
        [AllowEmptyString()][string]$Value,
        [switch]$UseNewlineCharacterReferences)

    $escaped = [Security.SecurityElement]::Escape($Value)
    if ($UseNewlineCharacterReferences) {
        return $escaped.Replace("`r`n", '&#xA;').Replace("`r", '&#xA;').Replace("`n", '&#xA;')
    }
    return $escaped.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
}

function Set-ResourceCatalogText {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Surface,
        [Parameter(Mandatory)]$RowsByIdentity,
        $ExistingValues)

    foreach ($pair in $RowsByIdentity.GetEnumerator()) {
        if (-not $pair.Key.StartsWith($Surface + "`0", [StringComparison]::Ordinal)) {
            continue
        }
        $key = $pair.Key.Substring($Surface.Length + 1)
        if ($null -ne $ExistingValues -and $ExistingValues.ContainsKey($key) -and
            [string]$ExistingValues[$key] -ceq [string]$pair.Value.Translation) {
            continue
        }
        $escapedKey = ConvertTo-ResourceXmlValue `
            $key -UseNewlineCharacterReferences
        $pattern = '(?s)(?<prefix><data\s+name="' + [regex]::Escape($escapedKey) +
            '"[^>]*>.*?<value>)(?<value>.*?)(?<suffix></value>)'
        $matches = [regex]::Matches($Text, $pattern)
        if ($matches.Count -ne 1) {
            throw "Resource template has $($matches.Count) value nodes for '$Surface/$key'."
        }
        $usesNewlineCharacterReferences = $matches[0].Groups['value'].Value -match
            '&#(?:xA|10);'
        $encoded = ConvertTo-ResourceXmlValue `
            ([string]$pair.Value.Translation) `
            -UseNewlineCharacterReferences:$usesNewlineCharacterReferences
        $Text = [regex]::Replace(
            $Text,
            $pattern,
            { param($match) $match.Groups['prefix'].Value + $encoded +
                $match.Groups['suffix'].Value },
            [Text.RegularExpressions.RegexOptions]::None,
            [TimeSpan]::FromSeconds(5))
    }
    return $Text
}

function Import-LocalizationWorkbook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Repository,
        [switch]$ValidateOnly)

    $Repository = Get-LocalizationRepository $Repository
    $rows = @(Read-TranslatorWorkbookRows $Path)
    $locale = [string]$rows[0].Locale
    Assert-TranslatorLocale $locale
    $expected = Get-LocalizationWorkbookModel $Repository $locale -ExcludeTranslations
    $expectedByIdentity = [Collections.Generic.Dictionary[string,object]]::new(
        [StringComparer]::Ordinal)
    foreach ($row in $expected.Rows) {
        $expectedByIdentity.Add(([string]$row.Surface + "`0" + [string]$row.Key), $row)
    }
    $actualByIdentity = [Collections.Generic.Dictionary[string,object]]::new(
        [StringComparer]::Ordinal)
    foreach ($row in $rows) {
        foreach ($required in @('FormatVersion', 'Locale', 'Surface', 'Key', 'Source',
                'Context', 'Constraints', 'Screenshots', 'SourceHash', 'CatalogHash',
                'TargetCatalogHash')) {
            if ([string]::IsNullOrEmpty([string]$row.$required)) {
                throw "Translator workbook row has a blank structural field '$required'."
            }
        }
        if ([string]$row.Locale -cne $locale) {
            throw 'Translator workbook mixes locales.'
        }
        $identity = [string]$row.Surface + "`0" + [string]$row.Key
        if ($actualByIdentity.ContainsKey($identity)) {
            throw "Translator workbook contains duplicate row '$($row.Surface)/$($row.Key)'."
        }
        $actualByIdentity.Add($identity, $row)
    }
    if ($actualByIdentity.Count -ne $expectedByIdentity.Count) {
        throw "Translator workbook row count $($actualByIdentity.Count) does not match current source count $($expectedByIdentity.Count)."
    }
    foreach ($pair in $expectedByIdentity.GetEnumerator()) {
        if (-not $actualByIdentity.ContainsKey($pair.Key)) {
            throw "Translator workbook is missing '$($pair.Value.Surface)/$($pair.Value.Key)'."
        }
        $actual = $actualByIdentity[$pair.Key]
        $canonical = $pair.Value
        foreach ($immutable in @('FormatVersion', 'Locale', 'Surface', 'Key', 'Source',
                'Context', 'Constraints', 'Screenshots', 'SourceHash', 'CatalogHash',
                'TargetCatalogHash')) {
            if ([string]$actual.$immutable -cne [string]$canonical.$immutable) {
                throw "Translator workbook row '$($canonical.Surface)/$($canonical.Key)' has stale or tampered '$immutable'. Re-export from current source."
            }
        }
        Assert-TranslationValue `
            -Source ([string]$canonical.Source) `
            -Translation ([string]$actual.Translation) `
            -Description "Translation '$($canonical.Surface)/$($canonical.Key)'"
    }
    Assert-AccessKeyGroups $actualByIdentity
    if ($ValidateOnly) {
        return [pscustomobject]@{
            Locale = $locale
            RowCount = $rows.Count
            CatalogHash = $expected.CatalogHash
            ChangedPaths = @()
        }
    }

    $currentTargetHash = Get-LocalizationTargetCatalogHash $Repository $locale
    if ($currentTargetHash -cne [string]$rows[0].TargetCatalogHash) {
        throw 'Translator workbook target catalogs changed during validation. Re-export from the current target catalogs.'
    }
    $originalFiles = [ordered]@{}
    $changedPaths = [Collections.Generic.List[string]]::new()
    try {
        foreach ($definition in @(Get-LocalizationCatalogDefinitions $Repository)) {
            $sourcePath = Join-Path $Repository $definition.SourcePath
            $relativeTarget = & $definition.TranslationPath $locale
            $targetPath = Join-Path $Repository $relativeTarget
            $exists = Test-Path -LiteralPath $targetPath -PathType Leaf
            $sourceCatalog = Read-LocalizationCatalog $sourcePath
            $targetCatalog = if ($exists) { Read-LocalizationCatalog $targetPath } else { $null }
            $useTargetTemplate = $null -ne $targetCatalog -and
                $targetCatalog.Values.Count -eq $sourceCatalog.Values.Count
            if ($useTargetTemplate) {
                foreach ($key in $sourceCatalog.Values.Keys) {
                    if (-not $targetCatalog.Values.ContainsKey($key)) {
                        $useTargetTemplate = $false
                        break
                    }
                }
            }
            $originalFiles[$targetPath] = if ($exists) {
                [IO.File]::ReadAllBytes($targetPath)
            } else { $null }
            $templateText = [IO.File]::ReadAllText(
                $(if ($useTargetTemplate) { $targetPath } else { $sourcePath }),
                [Text.UTF8Encoding]::new($false, $true))
            $updatedText = Set-ResourceCatalogText `
                -Text $templateText `
                -Surface $definition.Surface `
                -RowsByIdentity $actualByIdentity `
                -ExistingValues $(if ($useTargetTemplate) {
                    $targetCatalog.Values
                } else { $null })
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($updatedText)
            if ($exists -and [Linq.Enumerable]::SequenceEqual(
                    [byte[]]$originalFiles[$targetPath], [byte[]]$bytes)) {
                continue
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath)) | Out-Null
            [IO.File]::WriteAllBytes($targetPath, $bytes)
            $changedPaths.Add($relativeTarget.Replace('\', '/'))
        }
        & (Join-Path $Repository 'build\Validate-LocalizationResources.ps1') `
            -Repository $Repository
    }
    catch {
        foreach ($path in $originalFiles.Keys) {
            if ($null -eq $originalFiles[$path]) {
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    Remove-Item -LiteralPath $path -Force
                }
            }
            else {
                [IO.File]::WriteAllBytes($path, [byte[]]$originalFiles[$path])
            }
        }
        throw
    }
    return [pscustomobject]@{
        Locale = $locale
        RowCount = $rows.Count
        CatalogHash = $expected.CatalogHash
        ChangedPaths = @($changedPaths)
    }
}

function Test-LocalizationScreenshotSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextDirectory,
        [string]$Repository)

    $Repository = Get-LocalizationRepository $Repository
    $map = Read-LocalizationScreenshotMap $Repository
    $expected = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($relative in @($map.screenshots | ForEach-Object { [string]$_ })) {
        if ([IO.Path]::IsPathRooted($relative) -or @($relative.Split('/')) -contains '..') {
            throw "Translator screenshot map contains unsafe path '$relative'."
        }
        [void]$expected.Add($relative)
        $path = Join-Path $ContextDirectory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Translator screenshot is missing or empty: $relative"
        }
    }
    $prefix = [IO.Path]::GetFullPath($ContextDirectory).TrimEnd('\') + '\'
    foreach ($image in @(Get-ChildItem -LiteralPath $ContextDirectory `
            -Filter '*.png' -File -Recurse)) {
        $relative = [IO.Path]::GetFullPath($image.FullName).Substring(
            $prefix.Length).Replace('\', '/')
        if (-not $expected.Contains($relative)) {
            throw "Translator screenshot is not declared in the map: $relative"
        }
    }
}

Export-ModuleMember -Function @(
    'Export-LocalizationWorkbook',
    'Import-LocalizationWorkbook',
    'Test-LocalizationScreenshotSet')
