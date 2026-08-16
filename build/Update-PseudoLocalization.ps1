[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

function ConvertTo-PseudoLocalizedText {
    param([Parameter(Mandatory)][string]$Text)

    $protected = [Collections.Generic.List[string]]::new()
    $accented = [regex]::Replace($Text, '\{[^{}]+\}', {
        param($match)
        $index = $protected.Count
        $protected.Add($match.Value)
        return "@@$index@@"
    })
    foreach ($replacement in @(
            @('a', 0x00E1), @('e', 0x00E9), @('i', 0x00ED),
            @('o', 0x00F3), @('u', 0x00FA), @('y', 0x00FD),
            @('A', 0x00C1), @('E', 0x00C9), @('I', 0x00CD),
            @('O', 0x00D3), @('U', 0x00DA), @('Y', 0x00DD))) {
        $accented = $accented.Replace(
            [string]$replacement[0],
            [string][char]$replacement[1])
    }
    $letterCount = ([regex]::Matches($accented, '[\p{L}]')).Count
    $expansion = '~' * [Math]::Max(2, [Math]::Ceiling($letterCount * 0.35))
    $result = "[!! $accented $expansion !!]"
    for ($index = 0; $index -lt $protected.Count; $index++) {
        $result = $result.Replace("@@$index@@", $protected[$index])
    }
    return $result
}

function Write-PseudoResource {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    [xml]$document = [IO.File]::ReadAllText(
        $Source,
        [Text.UTF8Encoding]::new($false, $true))
    foreach ($data in @($document.root.data)) {
        $data.value = ConvertTo-PseudoLocalizedText ([string]$data.value)
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $writer = [Xml.XmlWriter]::Create($Destination, $settings)
    try { $document.Save($writer) }
    finally { $writer.Dispose() }
}

Write-PseudoResource `
    -Source (Join-Path $repository 'src\OpenBlade.Tray\Strings\en-US\Resources.resw') `
    -Destination (Join-Path $repository 'src\OpenBlade.Tray\Strings\qps-ploc\Resources.resw')
Write-PseudoResource `
    -Source (Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx') `
    -Destination (Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx')

Write-Host 'Updated qps-ploc resources from authoritative en-US catalogs.'
