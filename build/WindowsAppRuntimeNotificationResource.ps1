$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Add-WindowsAppRuntimeNotificationResource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)][string]$RuntimeVersion,
        [string]$NuGetPackageRoot
    )

    $packageRoot = if ([string]::IsNullOrWhiteSpace($NuGetPackageRoot)) {
        if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
            Join-Path (
                [Environment]::GetFolderPath(
                    [Environment+SpecialFolder]::UserProfile)) '.nuget\packages'
        }
        else {
            $env:NUGET_PACKAGES
        }
    }
    else {
        [IO.Path]::GetFullPath($NuGetPackageRoot)
    }

    $msix = Join-Path $packageRoot (
        ('microsoft.windowsappsdk.runtime\{0}\tools\MSIX\win10-x64\' `
            + 'Microsoft.WindowsAppRuntime.2.msix') -f $RuntimeVersion)
    if (-not (Test-Path -LiteralPath $msix -PathType Leaf)) {
        throw "Windows App Runtime package is missing its x64 framework MSIX: $msix"
    }

    $destination = Join-Path (
        [IO.Path]::GetFullPath($Payload)) 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
    $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $archive = $null
    $entryStream = $null
    $outputStream = $null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($msix)
        $entry = $archive.GetEntry('Microsoft.WindowsAppRuntime.Insights.Resource.dll')
        if ($null -eq $entry -or $entry.Length -le 0) {
            throw "Windows App Runtime MSIX is missing Microsoft.WindowsAppRuntime.Insights.Resource.dll."
        }

        $entryStream = $entry.Open()
        $outputStream = [IO.File]::Create($temporary)
        $entryStream.CopyTo($outputStream)
        $outputStream.Flush($true)
        $outputStream.Dispose()
        $outputStream = $null
        $entryStream.Dispose()
        $entryStream = $null
        $archive.Dispose()
        $archive = $null
        Move-Item -LiteralPath $temporary -Destination $destination -Force
    }
    finally {
        if ($null -ne $outputStream) {
            $outputStream.Dispose()
        }
        if ($null -ne $entryStream) {
            $entryStream.Dispose()
        }
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }

    $resourceValid = (Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-Item -LiteralPath $destination).Length -gt 0
    if (-not $resourceValid) {
        throw "Windows App Runtime notification resource extraction did not produce $destination"
    }
}
