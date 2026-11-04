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

Import-Module (Join-Path $PSScriptRoot 'localization\WinUiLocalization.psm1') -Force
$canonical = [ordered]@{}
if (Test-Path -LiteralPath $resourcePath -PathType Leaf) {
    [xml]$existingDocument = [IO.File]::ReadAllText($resourcePath, [Text.UTF8Encoding]::new($false, $true))
    foreach ($entry in @($existingDocument.root.data)) {
        $key = [string]$entry.name
        if ($canonical.Contains($key)) { throw "WinUI catalog contains duplicate key '$key'." }
        $canonical[$key] = [string]$entry.value
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
$usedPrefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($key in $canonical.Keys) { [void]$usedPrefixes.Add(($key -split '\.', 2)[0]) }
foreach ($relativePath in $files) {
    $path = Join-Path $repository $relativePath
    $text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
    $filePrefix = [IO.Path]::GetFileNameWithoutExtension($path)
    $edits = [Collections.Generic.List[object]]::new()
    foreach ($tag in @(Get-WinUiLocalizationTags $text $relativePath)) {
        $prefix = $tag.Uid
        $references = @($tag.Properties | Where-Object Kind -eq 'Resource')
        if ($references.Count -gt 0) { $prefix = $references[0].Prefix }
        foreach ($property in $tag.Properties) {
            if ($property.Kind -eq 'Resource') {
                $key = $property.Key
                if (-not $canonical.Contains($key)) {
                    throw "$relativePath requires missing en-US resource '$key'; add its authoritative English value first."
                }
                $value = $canonical[$key]
            }
            else {
                $value = $property.Value
                if ($property.Property -eq 'Title' -and ($tag.Tag -replace '^.*:', '') -eq 'Window') {
                    $resources[$filePrefix + '_WindowTitle'] = $value
                    continue
                }
                if ([string]::IsNullOrWhiteSpace($prefix)) {
                    $base = if ($tag.Name) { $tag.Name + 'Text' }
                        else { ($tag.Tag -replace '^.*:', '') + (ConvertTo-Identifier $value) }
                    $prefix = $filePrefix + '_' + $base
                    $suffix = 2
                    while (-not $usedPrefixes.Add($prefix)) {
                        $prefix = $filePrefix + '_' + $base + $suffix
                        $suffix++
                    }
                }
                $key = $prefix + '.' + $property.ResourceProperty
                $edits.Add([pscustomobject]@{
                    Index = $property.Index; Length = $property.Length
                    Value = '{CustomResource ' + $key + '}'
                })
            }
            if ($resources.Contains($key) -and $resources[$key] -cne $value) {
                throw "Resource key '$key' maps to conflicting values."
            }
            $resources[$key] = $value
        }
        if ($tag.UidIndex -ge 0) {
            # Consume the preceding space as well; preserve all other XAML layout.
            $index = $tag.UidIndex
            if ($index -gt 0 -and $text[$index - 1] -eq ' ') { $index-- }
            $edits.Add([pscustomobject]@{
                Index = $index; Length = $tag.UidLength + $tag.UidIndex - $index; Value = ''
            })
        }
    }
    $updated = $text
    foreach ($edit in @($edits | Sort-Object Index -Descending)) {
        $updated = $updated.Remove($edit.Index, $edit.Length).Insert($edit.Index, $edit.Value)
    }
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
