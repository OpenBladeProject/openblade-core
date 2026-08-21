[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Workbook,

    [string]$Repository,

    [switch]$ValidateOnly
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = Split-Path -Parent $PSScriptRoot
}
$Repository = [IO.Path]::GetFullPath($Repository)
$resolvedWorkbook = if ([IO.Path]::IsPathRooted($Workbook)) {
    [IO.Path]::GetFullPath($Workbook)
}
else {
    [IO.Path]::GetFullPath((Join-Path $Repository $Workbook))
}

Import-Module (Join-Path $PSScriptRoot 'LocalizationTranslatorWorkflow.psm1') -Force
$result = Import-LocalizationWorkbook `
    -Repository $Repository `
    -Path $resolvedWorkbook `
    -ValidateOnly:$ValidateOnly

$operation = if ($ValidateOnly) { 'Validated' } else { 'Imported' }
Write-Host "$operation $($result.RowCount) rows for $($result.Locale)."
Write-Host "Catalog hash: $($result.CatalogHash)"
if (@($result.ChangedPaths).Count -gt 0) {
    Write-Host 'Updated catalogs:'
    @($result.ChangedPaths) | ForEach-Object { Write-Host "  $_" }
}
