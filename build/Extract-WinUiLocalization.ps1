[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$resourceDirectory = Join-Path $repository 'src\OpenBlade.Tray\Strings\en-US'
$resourcePath = Join-Path $resourceDirectory 'Resources.resw'
$files = @(
    'src\OpenBlade.Tray\MainWindow.xaml',
    'src\OpenBlade.Tray\TrayDashboardWindow.xaml'
)

function ConvertTo-Identifier {
    param([Parameter(Mandatory)][string]$Value)

    $words = [regex]::Matches($Value, '[\p{L}\p{Nd}]+') |
        Select-Object -First 7 |
        ForEach-Object {
            $word = $_.Value
            if ($word.Length -eq 1) { $word.ToUpperInvariant() }
            else { $word.Substring(0, 1).ToUpperInvariant() + $word.Substring(1) }
        }
    $identifier = $words -join ''
    if ([string]::IsNullOrWhiteSpace($identifier)) {
        return 'LocalizedText'
    }
    if ($identifier[0] -match '\d') {
        return 'Value' + $identifier
    }
    return $identifier
}

function ConvertTo-ResourceProperty {
    param([Parameter(Mandatory)][string]$Property)

    switch ($Property) {
        'AutomationProperties.Name' {
            return '[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name'
        }
        'AutomationProperties.HelpText' {
            return '[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText'
        }
        'ToolTipService.ToolTip' {
            return '[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip'
        }
        default { return $Property }
    }
}

$resources = [ordered]@{}
$codePattern = 'LocalizedResources\.(?:Get|Format)\(\s*"(?<key>(?:[^"\\]|\\.)*)"\s*,\s*"(?<value>(?:[^"\\]|\\.)*)"'
foreach ($codeFile in @(Get-ChildItem (Join-Path $repository 'src\OpenBlade.Tray') `
        -Filter '*.cs' -File -Recurse | Where-Object FullName -notmatch '\\(?:bin|obj)\\')) {
    $code = [IO.File]::ReadAllText($codeFile.FullName, [Text.UTF8Encoding]::new($false, $true))
    foreach ($match in [regex]::Matches($code, $codePattern)) {
        $key = [Text.RegularExpressions.Regex]::Unescape($match.Groups['key'].Value)
        $value = [Text.RegularExpressions.Regex]::Unescape($match.Groups['value'].Value)
        if ($resources.Contains($key) -and $resources[$key] -ne $value) {
            throw "Runtime resource key '$key' maps to conflicting fallbacks."
        }
        $resources[$key] = $value
    }
}
$usedUids = @{}
foreach ($relativePath in $files) {
    $path = Join-Path $repository $relativePath
    $text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
    $filePrefix = [IO.Path]::GetFileNameWithoutExtension($path)
    $startTagPattern = '(?s)<(?<tag>[A-Za-z][\w:.]*)(?<attributes>(?:[^>"'']|"[^"]*"|''[^'']*'')*)>'
    $updated = [regex]::Replace($text, $startTagPattern, {
        param($tagMatch)

        $attributes = $tagMatch.Groups['attributes'].Value
        $localizable = @([regex]::Matches(
            $attributes,
            '(?<![\w.:])(?<property>Text|Header|Content|PlaceholderText|Title|AccessibleName|AccessKey|AutomationProperties\.(?:Name|HelpText)|ToolTipService\.ToolTip)="(?<value>[^"{}]+)"') |
            Where-Object {
                $value = $_.Groups['value'].Value
                -not [string]::IsNullOrWhiteSpace($value) -and
                $value -ne 'OpenBlade' -and
                $value -notmatch '^\s*(?:%|°C|RPM|MHz|W|ms|s|dB|Kbps|Hz|×|·)\s*$' -and
                $value -notmatch '^\d+(?:\.\d+)?$'
            })
        $windowTitle = @($localizable | Where-Object {
            $_.Groups['property'].Value -eq 'Title' -and
            ($tagMatch.Groups['tag'].Value -replace '^.*:', '') -eq 'Window'
        })
        $localizable = @($localizable | Where-Object { $windowTitle -notcontains $_ })
        foreach ($title in $windowTitle) {
            $resources[$filePrefix + '_WindowTitle'] =
                [Net.WebUtility]::HtmlDecode($title.Groups['value'].Value)
        }
        if ($localizable.Count -eq 0) {
            return $tagMatch.Value
        }

        $uidMatch = [regex]::Match($attributes, 'x:Uid="(?<uid>[^"]+)"')
        if ($uidMatch.Success) {
            $uid = $uidMatch.Groups['uid'].Value
        }
        else {
            $nameMatch = [regex]::Match($attributes, 'x:Name="(?<name>[^"]+)"')
            $base = if ($nameMatch.Success) {
                $nameMatch.Groups['name'].Value + 'Text'
            }
            else {
                ($tagMatch.Groups['tag'].Value -replace '^.*:', '') +
                    (ConvertTo-Identifier $localizable[0].Groups['value'].Value)
            }
            $uid = $filePrefix + '_' + $base
            $suffix = 2
            while ($usedUids.ContainsKey($uid)) {
                $uid = $filePrefix + '_' + $base + $suffix
                $suffix++
            }
            $usedUids[$uid] = $true
            $tagName = $tagMatch.Groups['tag'].Value
            $replacement = '<' + $tagName + ' x:Uid="' + $uid + '"' + $attributes + '>'
            $tagMatch = [regex]::Match($replacement, $startTagPattern)
        }

        foreach ($attribute in $localizable) {
            $property = ConvertTo-ResourceProperty $attribute.Groups['property'].Value
            $key = $uid + '.' + $property
            $value = [Net.WebUtility]::HtmlDecode($attribute.Groups['value'].Value)
            if ($resources.Contains($key) -and $resources[$key] -ne $value) {
                throw "Resource key '$key' maps to conflicting values."
            }
            $resources[$key] = $value
        }
        return $tagMatch.Value
    })
    if ($updated -cne $text) {
        [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
    }
}

[IO.Directory]::CreateDirectory($resourceDirectory) | Out-Null
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [Xml.NewLineHandling]::Replace
$existingKeys = @()
if (Test-Path -LiteralPath $resourcePath -PathType Leaf) {
    [xml]$existingDocument = [IO.File]::ReadAllText(
        $resourcePath,
        [Text.UTF8Encoding]::new($false, $true))
    $existingKeys = @($existingDocument.root.data | ForEach-Object { [string]$_.name })
}
$writer = [Xml.XmlWriter]::Create($resourcePath, $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('root')
    foreach ($header in @(
            @('resmimetype', 'text/microsoft-resx'),
            @('version', '2.0'),
            @('reader', 'System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),
            @('writer', 'System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'))) {
        $writer.WriteStartElement('resheader')
        $writer.WriteAttributeString('name', $header[0])
        $writer.WriteElementString('value', $header[1])
        $writer.WriteEndElement()
    }
    $keys = [Collections.Generic.List[string]]::new()
    $resourceKeys = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $resources.Keys) {
        $resourceKeys.Add([string]$key, [string]$key)
    }
    $seenKeys = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $existingKeys) {
        $currentKey = $null
        if ($resourceKeys.TryGetValue($key, [ref]$currentKey) -and
            $seenKeys.Add($currentKey)) {
            $keys.Add($currentKey)
        }
    }
    $newKeys = [Collections.Generic.List[string]]::new()
    foreach ($key in $resources.Keys) {
        if ($seenKeys.Add([string]$key)) {
            $newKeys.Add([string]$key)
        }
    }
    $newKeys.Sort([StringComparer]::Ordinal)
    $keys.AddRange($newKeys)
    foreach ($key in $keys) {
        $writer.WriteStartElement('data')
        $writer.WriteAttributeString('name', $key)
        $writer.WriteAttributeString('xml', 'space', $null, 'preserve')
        $writer.WriteElementString('value', [string]$resources[$key])
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteWhitespace("`r`n")
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

Write-Host "Extracted $($resources.Count) WinUI resource properties."
