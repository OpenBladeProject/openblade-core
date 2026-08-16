[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'build\installer\OpenBlade.Installer.UI'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$xaml = [IO.File]::ReadAllText((Join-Path $project 'InstallerWindow.xaml'), $utf8)
$code = [IO.File]::ReadAllText((Join-Path $project 'InstallerWindow.xaml.cs'), $utf8)
$values = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($match in [regex]::Matches(
        $xaml,
        '(?:Text|Header|Content|Title|AutomationProperties\.(?:Name|HelpText))="(?<value>[^"{}]+)"')) {
    $value = [Net.WebUtility]::HtmlDecode($match.Groups['value'].Value)
    if (-not [string]::IsNullOrWhiteSpace($value) -and $value -notmatch '^\d+(?:\.\d+)?$') {
        [void]$values.Add($value)
    }
}
foreach ($match in [regex]::Matches(
        $code,
        '(?<=[(?,:\s])"(?<value>(?:[^"\\]|\\.)*)"')) {
    $value = [Text.RegularExpressions.Regex]::Unescape($match.Groups['value'].Value)
    if (-not [string]::IsNullOrWhiteSpace($value) -and $value -match '[\p{L}]') {
        [void]$values.Add($value)
    }
}

$resourceDirectory = Join-Path $project 'Properties'
$resourcePath = Join-Path $resourceDirectory 'Resources.resx'
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [Xml.NewLineHandling]::Replace
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
    foreach ($value in $values | Sort-Object) {
        $writer.WriteStartElement('data')
        $writer.WriteAttributeString('name', $value)
        $writer.WriteAttributeString('xml', 'space', $null, 'preserve')
        $writer.WriteElementString('value', $value)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

Write-Host "Extracted $($values.Count) installer resource strings."
