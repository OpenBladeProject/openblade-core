[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Locale,

    [string]$OutputDirectory = 'artifacts\localization\translator',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Force
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'LocalizationTranslatorWorkflow.psm1') -Force

Assert-TranslatorLocale $Locale

$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
}
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$packageName = "OpenBlade-$Locale-translator"
$packagePath = Join-Path $outputRoot $packageName
$archivePath = Join-Path $outputRoot "$packageName.zip"
if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath $archivePath)) {
    if (-not $Force) {
        throw "Translator package already exists. Use -Force to replace: $packagePath"
    }
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Recurse -Force
    }
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}

$temporaryPath = Join-Path $outputRoot ('.' + $packageName + '.' +
    [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temporaryPath) | Out-Null
    $workbookName = "OpenBlade-$Locale.csv"
    $workbookPath = Join-Path $temporaryPath $workbookName
    $model = Export-LocalizationWorkbook `
        -Repository $repository `
        -Locale $Locale `
        -Path $workbookPath

    $readme = @"
OpenBlade translation package for $Locale

Open $workbookName in Excel or LibreOffice Calc. Edit only the Translation
column. Keep every other column and every row. Save as UTF-8 CSV, then return
the CSV to an OpenBlade maintainer. See docs/localization-translators.md in the
OpenBlade repository for step-by-step instructions and validation help.

Screenshot paths in the Screenshots column are relative to this folder. The
images contain fixed sample data and are generated without installing or
controlling hardware.
"@
    [IO.File]::WriteAllText(
        (Join-Path $temporaryPath 'README.txt'),
        $readme.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n"),
        [Text.UTF8Encoding]::new($false))

    $captureRoot = Join-Path $temporaryPath '.snapshot-source'
    $powershell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') {
        'pwsh.exe'
    } else {
        'powershell.exe'
    })
    & $powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'Capture-LocalizationSnapshots.ps1') `
        -OutputDirectory $captureRoot `
        -Configuration $Configuration `
        -Culture 'en-US'
    if ($LASTEXITCODE -ne 0) {
        throw 'Localization context snapshot generation failed.'
    }
    $capturedCulture = Join-Path $captureRoot 'en-US'
    $captureManifest = Get-Content `
        -LiteralPath (Join-Path $capturedCulture 'manifest.json') `
        -Raw | ConvertFrom-Json
    if ($captureManifest.schemaVersion -ne 2 -or
        [string]$captureManifest.culture -cne 'en-US' -or
        [string]$captureManifest.renderProfile.dpi -cne
            '96-process-local-DPIUNAWARE') {
        throw 'Localization snapshot manifest has an unexpected render profile.'
    }
    $screenshotRenderProfile = $captureManifest.renderProfile
    $contextPath = Join-Path $temporaryPath 'context'
    [IO.Directory]::CreateDirectory($contextPath) | Out-Null
    foreach ($image in @(Get-ChildItem -LiteralPath $capturedCulture `
            -Filter '*.png' -File -Recurse | Sort-Object FullName)) {
        $prefix = [IO.Path]::GetFullPath($capturedCulture).TrimEnd('\') + '\'
        $relative = [IO.Path]::GetFullPath($image.FullName).Substring($prefix.Length)
        $destination = Join-Path $contextPath $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) |
            Out-Null
        Copy-Item -LiteralPath $image.FullName -Destination $destination
    }
    Test-LocalizationScreenshotSet `
        -Repository $repository `
        -ContextDirectory $contextPath
    Remove-Item -LiteralPath $captureRoot -Recurse -Force

    $manifestImages = @(Get-ChildItem (Join-Path $temporaryPath 'context') `
        -Filter '*.png' -File -Recurse | Sort-Object FullName | ForEach-Object {
            $prefix = [IO.Path]::GetFullPath($temporaryPath).TrimEnd('\') + '\'
            [ordered]@{
                path = [IO.Path]::GetFullPath($_.FullName).Substring(
                    $prefix.Length).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).
                    Hash.ToLowerInvariant()
                bytes = $_.Length
            }
        })
    $manifest = [ordered]@{
        schemaVersion = 1
        locale = $Locale
        workbook = $workbookName
        workbookSha256 = (Get-FileHash -LiteralPath $workbookPath -Algorithm SHA256).
            Hash.ToLowerInvariant()
        rowCount = @($model.Rows).Count
        catalogHash = $model.CatalogHash
        targetCatalogHash = $model.TargetCatalogHash
        screenshotsIncluded = $true
        screenshotRenderProfile = $screenshotRenderProfile
        screenshots = $manifestImages
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    $manifestJson = $manifestJson.Replace("`r`n", "`n").Replace("`r", "`n") + "`n"
    [IO.File]::WriteAllText(
        (Join-Path $temporaryPath 'manifest.json'),
        $manifestJson,
        [Text.UTF8Encoding]::new($false))

    Move-Item -LiteralPath $temporaryPath -Destination $packagePath
    $temporaryPath = $null

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveStream = [IO.File]::Open(
        $archivePath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false,
            [Text.Encoding]::UTF8)
        try {
            $prefix = [IO.Path]::GetFullPath($packagePath).TrimEnd('\') + '\'
            foreach ($file in @(Get-ChildItem -LiteralPath $packagePath -File -Recurse |
                    Sort-Object FullName)) {
                $relative = [IO.Path]::GetFullPath($file.FullName).Substring(
                    $prefix.Length).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $relative,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) }
                finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $archiveStream.Dispose() }

    Write-Host "Translator package: $packagePath"
    Write-Host "Distributable archive: $archivePath"
}
finally {
    if ($null -ne $temporaryPath -and (Test-Path -LiteralPath $temporaryPath)) {
        $resolved = [IO.Path]::GetFullPath($temporaryPath)
        $prefix = [IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unexpected translator package cleanup target: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
