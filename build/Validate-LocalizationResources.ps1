[CmdletBinding()]
param(
    [string]$Repository
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = Split-Path -Parent $PSScriptRoot
}
$Repository = [IO.Path]::GetFullPath($Repository)

function Get-RepositoryRelativePath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the localization repository: $fullPath"
    }
    return $fullPath.Substring($prefix.Length)
}

function Assert-ValidFormatString {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Description
    )

    $indices = @([regex]::Matches($Value, '\{(?<index>\d+)') |
        ForEach-Object { [int]$_.Groups['index'].Value })
    $count = if ($indices.Count -eq 0) { 0 } else { 1 + ($indices | Measure-Object -Maximum).Maximum }
    [object[]]$arguments = @('value') * $count
    try {
        [void][string]::Format(
            [Globalization.CultureInfo]::InvariantCulture,
            $Value,
            $arguments)
    }
    catch [FormatException] {
        throw "$Description contains an invalid format string."
    }
}

function Read-ResourceCatalog {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Localization catalog is missing: $Path"
    }
    try {
        [xml]$document = [IO.File]::ReadAllText(
            $Path,
            [Text.UTF8Encoding]::new($false, $true))
    }
    catch { throw "Localization catalog is malformed: $Path. $($_.Exception.Message)" }

    $catalog = [ordered]@{}
    foreach ($data in @($document.root.data)) {
        $key = [string]$data.name
        $value = [string]$data.value
        if ([string]::IsNullOrWhiteSpace($key)) {
            throw "Localization catalog contains a blank key: $Path"
        }
        if ($catalog.Contains($key)) {
            throw "Localization catalog contains duplicate key '$key': $Path"
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "Localization catalog contains blank value '$key': $Path"
        }
        Assert-ValidFormatString $value "Localization resource '$key' in $Path"
        $catalog[$key] = $value
    }
    return $catalog
}

function Get-Placeholders {
    param([Parameter(Mandatory)][string]$Value)
    return @([regex]::Matches($Value, '\{\d+(?:[^{}]*)\}') |
        ForEach-Object Value | Sort-Object)
}

function Get-AccessKeys {
    param([Parameter(Mandatory)][string]$Value)
    return @([regex]::Matches($Value, '(?<!_)_(?!_)(?<key>[\p{L}\p{Nd}])') |
        ForEach-Object { $_.Groups['key'].Value.ToUpperInvariant() } |
        Sort-Object)
}

function Assert-PseudoCatalog {
    param(
        [Parameter(Mandatory)]$Authoritative,
        [Parameter(Mandatory)]$Pseudo,
        [Parameter(Mandatory)][string]$Name
    )

    $difference = @(Compare-Object @($Authoritative.Keys) @($Pseudo.Keys))
    if ($difference.Count -ne 0) {
        throw "$Name pseudo catalog key mismatch: $($difference -join ', ')"
    }
    foreach ($key in $Authoritative.Keys) {
        $authoritativeValue = [string]$Authoritative[$key]
        $pseudoValue = [string]$Pseudo[$key]
        if ($authoritativeValue -notmatch '\p{L}') {
            if ($pseudoValue -cne $authoritativeValue) {
                throw "$Name pseudo resource '$key' changed punctuation-only text."
            }
        }
        elseif (-not [regex]::IsMatch($pseudoValue, '^\[!!(?: |\r?\n)')) {
            throw "$Name pseudo resource '$key' is not visibly pseudo-localized."
        }
        $expectedPlaceholders = @(Get-Placeholders $authoritativeValue)
        $actualPlaceholders = @(Get-Placeholders $pseudoValue)
        if (@(Compare-Object $expectedPlaceholders $actualPlaceholders).Count -ne 0) {
            throw "$Name pseudo resource '$key' changed its format placeholders."
        }
        $expectedAccessKeys = @(Get-AccessKeys $authoritativeValue)
        $actualAccessKeys = @(Get-AccessKeys $pseudoValue)
        if (@(Compare-Object $expectedAccessKeys $actualAccessKeys).Count -ne 0) {
            throw "$Name pseudo resource '$key' changed its access key."
        }
    }
}

function Assert-TranslationCatalog {
    param(
        [Parameter(Mandatory)]$Authoritative,
        [Parameter(Mandatory)]$Translation,
        [Parameter(Mandatory)][string]$Name
    )

    foreach ($key in $Translation.Keys) {
        if (-not $Authoritative.Contains($key)) {
            throw "$Name contains orphan resource '$key'."
        }
        $expected = @(Get-Placeholders ([string]$Authoritative[$key]))
        $actual = @(Get-Placeholders ([string]$Translation[$key]))
        if (@(Compare-Object $expected $actual).Count -ne 0) {
            throw "$Name resource '$key' changed its format placeholders."
        }
    }
}

$trayEnglishPath = Join-Path $Repository 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
$trayPseudoPath = Join-Path $Repository 'src\OpenBlade.Tray\Strings\qps-ploc\Resources.resw'
$installerEnglishPath = Join-Path $Repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx'
$installerPseudoPath = Join-Path $Repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx'
$trayEnglish = Read-ResourceCatalog $trayEnglishPath
$trayPseudo = Read-ResourceCatalog $trayPseudoPath
$installerEnglish = Read-ResourceCatalog $installerEnglishPath
$installerPseudo = Read-ResourceCatalog $installerPseudoPath
Assert-PseudoCatalog $trayEnglish $trayPseudo 'Tray'
Assert-PseudoCatalog $installerEnglish $installerPseudo 'Installer'

$optionalCatalogs = @(
    @('Tray Core', 'src\OpenBlade.Tray.Core\Properties\Resources.resx', 'src\OpenBlade.Tray.Core\Properties\Resources.qps-ploc.resx'),
    @('Setup', 'src\OpenBlade.Setup\Properties\Resources.resx', 'src\OpenBlade.Setup\Properties\Resources.qps-ploc.resx'))
foreach ($catalog in $optionalCatalogs) {
    $englishPath = Join-Path $Repository $catalog[1]
    if (Test-Path -LiteralPath $englishPath -PathType Leaf) {
        $english = Read-ResourceCatalog $englishPath
        $pseudo = Read-ResourceCatalog (Join-Path $Repository $catalog[2])
        Assert-PseudoCatalog $english $pseudo $catalog[0]
        $directory = [IO.Path]::GetDirectoryName($englishPath)
        foreach ($translationPath in @(Get-ChildItem $directory `
                -Filter 'Resources.*.resx' -File | Where-Object Name -ne 'Resources.qps-ploc.resx')) {
            Assert-TranslationCatalog $english (Read-ResourceCatalog $translationPath.FullName) `
                "$($catalog[0]) translation $($translationPath.FullName)"
        }
    }
}

function Assert-ManagedResourceCalls {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$Accessor,
        [Parameter(Mandatory)][string]$CatalogPath
    )

    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) { return }
    $catalog = Read-ResourceCatalog $CatalogPath
    $pattern = [regex]::Escape($Accessor) +
        '\.(?:Get|Format)\(\s*"(?<key>(?:[^"\\]|\\.)*)"'
    foreach ($codePath in @(Get-ChildItem $SourceRoot -Filter '*.cs' -File -Recurse |
            Where-Object FullName -notmatch '\\(?:bin|obj)\\')) {
        $code = [IO.File]::ReadAllText(
            $codePath.FullName,
            [Text.UTF8Encoding]::new($false, $true))
        foreach ($match in [regex]::Matches($code, $pattern)) {
            $key = [Text.RegularExpressions.Regex]::Unescape($match.Groups['key'].Value)
            if (-not $catalog.Contains($key)) {
                throw "$($codePath.FullName) requires missing managed resource '$key'."
            }
            if ($catalog[$key] -ne $key) {
                throw "$CatalogPath must use authoritative en-US value '$key'."
            }
        }
    }
}

Assert-ManagedResourceCalls `
    (Join-Path $Repository 'src\OpenBlade.Tray.Core') `
    'TrayText' `
    (Join-Path $Repository 'src\OpenBlade.Tray.Core\Properties\Resources.resx')
Assert-ManagedResourceCalls `
    (Join-Path $Repository 'src\OpenBlade.Setup') `
    'SetupStrings' `
    (Join-Path $Repository 'src\OpenBlade.Setup\Properties\Resources.resx')

$bundleLocalizationPath = Join-Path $Repository 'build\installer\Bundle.en-US.wxl'
try {
    [xml]$bundleLocalization = [IO.File]::ReadAllText(
        $bundleLocalizationPath,
        [Text.UTF8Encoding]::new($false, $true))
}
catch { throw "Bundle localization is malformed: $($_.Exception.Message)" }
$bundleCatalog = @{}
foreach ($entry in @($bundleLocalization.DocumentElement.ChildNodes |
        Where-Object LocalName -eq 'String')) {
    $key = [string]$entry.Id
    $value = [string]$entry.InnerText
    if ([string]::IsNullOrWhiteSpace($key) -or [string]::IsNullOrWhiteSpace($value)) {
        throw 'Bundle localization contains a blank key or value.'
    }
    if ($bundleCatalog.ContainsKey($key)) {
        throw "Bundle localization contains duplicate key '$key'."
    }
    $bundleCatalog[$key] = $value
}
$bundleSource = [IO.File]::ReadAllText(
    (Join-Path $Repository 'build\installer\Bundle.wxs'),
    [Text.UTF8Encoding]::new($false, $true))
foreach ($match in [regex]::Matches($bundleSource, '!\(loc\.(?<key>[^)]+)\)')) {
    $key = $match.Groups['key'].Value
    if (-not $bundleCatalog.ContainsKey($key)) {
        throw "Bundle source requires missing en-US localization '$key'."
    }
}

foreach ($translationPath in @(Get-ChildItem (
        Join-Path $Repository 'src\OpenBlade.Tray\Strings') -Directory |
        Where-Object Name -notin @('en-US', 'qps-ploc') |
        ForEach-Object { Join-Path $_.FullName 'Resources.resw' })) {
    Assert-TranslationCatalog $trayEnglish (Read-ResourceCatalog $translationPath) `
        "Tray translation $translationPath"
}

$seenUids = @{}
$requiredTrayKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in @(Get-ChildItem (Join-Path $Repository 'src\OpenBlade.Tray') `
        -Filter '*.xaml' -File -Recurse | Where-Object FullName -notmatch '\\(?:bin|obj)\\')) {
    $relativePath = Get-RepositoryRelativePath $Repository $path.FullName
    $text = [IO.File]::ReadAllText($path.FullName, [Text.UTF8Encoding]::new($false, $true))
    $startTagPattern = '(?s)<(?<tag>[A-Za-z][\w:.]*)(?<attributes>(?:[^>"'']|"[^"]*"|''[^'']*'')*)>'
    foreach ($tag in [regex]::Matches($text, $startTagPattern)) {
        $attributes = $tag.Groups['attributes'].Value
        $localizable = @([regex]::Matches(
            $attributes,
            '(?<property>Text|Header|Content|PlaceholderText|Title|AccessibleName|AccessKey|AutomationProperties\.(?:Name|HelpText)|ToolTipService\.ToolTip)="(?<value>[^"{}]+)"') |
            Where-Object {
                $value = $_.Groups['value'].Value
                -not [string]::IsNullOrWhiteSpace($value) -and
                $value -ne 'OpenBlade' -and
                $value -notmatch '^\s*(?:%|°C|RPM|MHz|W|ms|s|dB|Kbps|Hz|×|·)\s*$' -and
                $value -notmatch '^\d+(?:\.\d+)?$'
            })
        $windowTitle = @($localizable | Where-Object {
            $_.Groups['property'].Value -eq 'Title' -and
            ($tag.Groups['tag'].Value -replace '^.*:', '') -eq 'Window'
        })
        $localizable = @($localizable | Where-Object { $windowTitle -notcontains $_ })
        foreach ($title in $windowTitle) {
            $windowTitleKey = [IO.Path]::GetFileNameWithoutExtension($relativePath) +
                '_WindowTitle'
            $sourceTitle = [Net.WebUtility]::HtmlDecode($title.Groups['value'].Value)
            if (-not $trayEnglish.Contains($windowTitleKey) -or
                $trayEnglish[$windowTitleKey] -ne $sourceTitle) {
                throw "$relativePath requires matching en-US resource '$windowTitleKey'."
            }
            [void]$requiredTrayKeys.Add($windowTitleKey)
        }
        if ($localizable.Count -eq 0) { continue }
        $uidMatch = [regex]::Match($attributes, 'x:Uid="(?<uid>[^"]+)"')
        if (-not $uidMatch.Success) {
            throw "$relativePath contains a user-facing XAML literal without x:Uid: $($tag.Value)"
        }
        $uid = $uidMatch.Groups['uid'].Value
        if ($seenUids.ContainsKey($uid)) {
            throw "WinUI localization x:Uid '$uid' is duplicated."
        }
        $seenUids[$uid] = $true
        foreach ($attribute in $localizable) {
            $property = switch ($attribute.Groups['property'].Value) {
                'AutomationProperties.Name' {
                    '[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name'
                }
                'AutomationProperties.HelpText' {
                    '[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText'
                }
                'ToolTipService.ToolTip' {
                    '[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip'
                }
                default { $_ }
            }
            $key = $uid + '.' + $property
            if (-not $trayEnglish.Contains($key)) {
                throw "$relativePath requires missing en-US resource '$key'."
            }
            [void]$requiredTrayKeys.Add($key)
            $sourceValue = [Net.WebUtility]::HtmlDecode($attribute.Groups['value'].Value)
            if ($trayEnglish[$key] -ne $sourceValue) {
                throw "$relativePath fallback text does not match authoritative resource '$key'."
            }
        }
    }
}

$installerXaml = [IO.File]::ReadAllText(
    (Join-Path $Repository 'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml'),
    [Text.UTF8Encoding]::new($false, $true))
foreach ($match in [regex]::Matches(
        $installerXaml,
        '(?:Text|Header|Content|Title|AutomationProperties\.(?:Name|HelpText))="(?<value>[^"{}]+)"')) {
    $value = [Net.WebUtility]::HtmlDecode($match.Groups['value'].Value)
    if (-not [string]::IsNullOrWhiteSpace($value) -and
        $value -notmatch '^\d+(?:\.\d+)?$' -and
        -not $installerEnglish.Contains($value)) {
        throw "Installer XAML requires missing en-US resource '$value'."
    }
}

$installerWindowCodePath =
    'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml.cs'
$installerWindowCode = [IO.File]::ReadAllText(
    (Join-Path $Repository $installerWindowCodePath),
    [Text.UTF8Encoding]::new($false, $true))
foreach ($match in [regex]::Matches(
        $installerWindowCode,
        '(?<=[(?,:\s])"(?<value>(?:[^"\\]|\\.)*)"')) {
    $value = [Text.RegularExpressions.Regex]::Unescape($match.Groups['value'].Value)
    if (-not [string]::IsNullOrWhiteSpace($value) -and
        $value -match '[\p{L}]' -and
        -not $installerEnglish.Contains($value)) {
        throw "$installerWindowCodePath requires missing en-US resource '$value'."
    }
}
$bootstrapperPath =
    'build\installer\OpenBlade.Installer.UI\OpenBladeBootstrapperApplication.cs'
$bootstrapperCode = [IO.File]::ReadAllText(
    (Join-Path $Repository $bootstrapperPath),
    [Text.UTF8Encoding]::new($false, $true))
foreach ($match in [regex]::Matches(
        $bootstrapperCode,
        'InstallerStrings\.(?:Get|Format)\(\s*"(?<value>(?:[^"\\]|\\.)*)"')) {
    $value = [Text.RegularExpressions.Regex]::Unescape($match.Groups['value'].Value)
    if (-not $installerEnglish.Contains($value)) {
        throw "$bootstrapperPath requires missing en-US resource '$value'."
    }
}

$trayCodePattern = 'LocalizedResources\.(?:Get|Format)\(\s*"(?<key>(?:[^"\\]|\\.)*)"\s*,\s*"(?<value>(?:[^"\\]|\\.)*)"'
foreach ($codePath in @(Get-ChildItem (Join-Path $Repository 'src\OpenBlade.Tray') `
        -Filter '*.cs' -File -Recurse | Where-Object FullName -notmatch '\\(?:bin|obj)\\')) {
    $code = [IO.File]::ReadAllText($codePath.FullName, [Text.UTF8Encoding]::new($false, $true))
    foreach ($match in [regex]::Matches($code, $trayCodePattern)) {
        $key = [Text.RegularExpressions.Regex]::Unescape($match.Groups['key'].Value)
        $fallback = [Text.RegularExpressions.Regex]::Unescape($match.Groups['value'].Value)
        if (-not $trayEnglish.Contains($key)) {
            throw "$($codePath.FullName) requires missing en-US resource '$key'."
        }
        if ($trayEnglish[$key] -ne $fallback) {
            throw "$($codePath.FullName) fallback does not match en-US resource '$key'."
        }
        [void]$requiredTrayKeys.Add($key)
    }
}
foreach ($key in $trayEnglish.Keys) {
    if (-not $requiredTrayKeys.Contains($key)) {
        throw "Tray en-US catalog contains orphan resource '$key'."
    }
}

Write-Host "Localization resources passed: $($trayEnglish.Count) tray properties and $($installerEnglish.Count) installer strings."
