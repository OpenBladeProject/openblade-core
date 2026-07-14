[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$bundle = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenSynapse'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifacts ('.staging\' + [Guid]::NewGuid().ToString('N'))))
$stagingBundle = Join-Path $stagingRoot 'OpenSynapse'
$payload = Join-Path $stagingBundle 'payload'

function Assert-SafeArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside the artifacts directory: $candidate"
    }
}

function Invoke-CheckedPublish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Output
    )

    & dotnet publish $Project @publishArguments --output $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE for $Project"
    }
}

Assert-SafeArtifactPath -Path $bundle
Assert-SafeArtifactPath -Path $stagingRoot

$publishArguments = @(
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--property:PublishTrimmed=false'
)

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    Invoke-CheckedPublish (Join-Path $repository 'src\OpenSynapse.Service\OpenSynapse.Service.csproj') $payload
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenSynapse.Tray\OpenSynapse.Tray.csproj') $payload
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenSynapse.Capture\OpenSynapse.Capture.csproj') $payload
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenSynapse.Setup\OpenSynapse.Setup.csproj') $stagingBundle

    if (Test-Path -LiteralPath $bundle) {
        Remove-Item -LiteralPath $bundle -Recurse -Force
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $bundle) -Force | Out-Null
    Move-Item -LiteralPath $stagingBundle -Destination $bundle
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-SafeArtifactPath -Path $stagingRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host "Self-contained bundle created at $bundle"
