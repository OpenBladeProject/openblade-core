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
        $catalog[$key] = $value
    }
    return $catalog
}

function Get-Placeholders {
    param([Parameter(Mandatory)][string]$Value)
    return @([regex]::Matches($Value, '\{\d+(?:[^{}]*)\}') |
        ForEach-Object Value | Sort-Object -Unique)
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
        if (-not ([string]$Pseudo[$key]).StartsWith('[!! ', [StringComparison]::Ordinal)) {
            throw "$Name pseudo resource '$key' is not visibly pseudo-localized."
        }
        $expectedPlaceholders = @(Get-Placeholders ([string]$Authoritative[$key]))
        $actualPlaceholders = @(Get-Placeholders ([string]$Pseudo[$key]))
        if (@(Compare-Object $expectedPlaceholders $actualPlaceholders).Count -ne 0) {
            throw "$Name pseudo resource '$key' changed its format placeholders."
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

$seenUids = @{}
foreach ($relativePath in @(
        'src\OpenBlade.Tray\MainWindow.xaml',
        'src\OpenBlade.Tray\TrayDashboardWindow.xaml')) {
    $path = Join-Path $Repository $relativePath
    $text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
    $startTagPattern = '(?s)<(?<tag>[A-Za-z][\w:.]*)(?<attributes>(?:[^>"'']|"[^"]*"|''[^'']*'')*)>'
    foreach ($tag in [regex]::Matches($text, $startTagPattern)) {
        $attributes = $tag.Groups['attributes'].Value
        $localizable = @([regex]::Matches(
            $attributes,
            '(?<property>Text|Header|Content|PlaceholderText|Title|AccessibleName|AutomationProperties\.Name|ToolTipService\.ToolTip)="(?<value>[^"{}]+)"') |
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
                'ToolTipService.ToolTip' {
                    '[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip'
                }
                default { $_ }
            }
            $key = $uid + '.' + $property
            if (-not $trayEnglish.Contains($key)) {
                throw "$relativePath requires missing en-US resource '$key'."
            }
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

$installerCode = [IO.File]::ReadAllText(
    (Join-Path $Repository 'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml.cs'),
    [Text.UTF8Encoding]::new($false, $true))
foreach ($match in [regex]::Matches(
        $installerCode,
        'InstallerStrings\.(?:Get|Format)\(\s*"(?<value>(?:[^"\\]|\\.)*)"')) {
    $value = [Text.RegularExpressions.Regex]::Unescape($match.Groups['value'].Value)
    if (-not $installerEnglish.Contains($value)) {
        throw "Installer code requires missing en-US resource '$value'."
    }
}

Write-Host "Localization resources passed: $($trayEnglish.Count) tray properties and $($installerEnglish.Count) installer strings."
