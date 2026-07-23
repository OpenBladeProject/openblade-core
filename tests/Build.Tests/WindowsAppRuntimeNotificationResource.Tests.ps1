[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\WindowsAppRuntimeNotificationResource.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function New-TestRuntimePackage {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Version,
        [switch]$WithoutResource
    )

    $directory = Join-Path $Root (
        'microsoft.windowsappsdk.runtime\{0}\tools\MSIX\win10-x64' -f $Version)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $msix = Join-Path $directory 'Microsoft.WindowsAppRuntime.2.msix'
    $stream = [IO.File]::Create($msix)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $entry = $archive.CreateEntry(
                $(if ($WithoutResource) { 'not-the-resource.txt' }
                    else { 'Microsoft.WindowsAppRuntime.Insights.Resource.dll' }))
            $writer = [IO.StreamWriter]::new($entry.Open())
            try {
                $writer.Write('signed-package-fixture')
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade.NotificationResource.Tests.' + [Guid]::NewGuid().ToString('N'))
try {
    $packageRoot = Join-Path $testRoot 'packages'
    $payload = Join-Path $testRoot 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    New-TestRuntimePackage -Root $packageRoot -Version '2.2.0'
    $assets = Join-Path $testRoot 'project.assets.json'
    @{
        libraries = @{
            'Microsoft.WindowsAppSDK.Runtime/2.2.0' = @{
                type = 'package'
            }
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $assets -Encoding UTF8
    Assert-True (
        (Get-WindowsAppRuntimeVersionFromAssets -AssetsPath $assets) -eq '2.2.0') `
        'The Windows App Runtime version was not read from NuGet package assets.'

    Add-WindowsAppRuntimeNotificationResource `
        -Payload $payload `
        -RuntimeVersion '2.2.0' `
        -NuGetPackageRoot $packageRoot
    $resource = Join-Path $payload 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
    Assert-True (Test-Path -LiteralPath $resource -PathType Leaf) `
        'The notification resource was not extracted.'
    Assert-True ((Get-Content -Raw -LiteralPath $resource) -eq 'signed-package-fixture') `
        'The extracted notification resource content changed.'

    $missingRoot = Join-Path $testRoot 'missing-packages'
    New-TestRuntimePackage -Root $missingRoot -Version '2.2.0' -WithoutResource
    $caught = $null
    try {
        Add-WindowsAppRuntimeNotificationResource `
            -Payload $payload `
            -RuntimeVersion '2.2.0' `
            -NuGetPackageRoot $missingRoot
    }
    catch {
        $caught = $_
    }
    Assert-True ($null -ne $caught) 'A runtime MSIX without the resource was accepted.'
    Assert-True ($caught.Exception.Message -match 'missing Microsoft.WindowsAppRuntime') `
        "The missing-resource failure was unclear: $($caught.Exception.Message)"

    Write-Host 'Windows App Runtime notification resource tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
