[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$TpmKeyName,
    [switch]$CreateTpmKey
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$oneDriveSegment =
    "$([IO.Path]::DirectorySeparatorChar)OneDrive$([IO.Path]::DirectorySeparatorChar)"
if ($repository.IndexOf($oneDriveSegment, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Build the local-development provisioner from a short non-OneDrive worktree.'
}

$artifacts = Join-Path $repository 'artifacts\win-x64'
$target = Join-Path $artifacts 'OpenBlade.LocalDevDeployment.Provisioner'
$stagingRoot = Join-Path $artifacts ('.local-dev-provisioner.' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stagingRoot 'package'

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The generated path escapes its fixed artifact root: $fullPath"
    }
}

function Invoke-SingleFilePublish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$ExecutableName
    )

    $projectOutput = Join-Path $stagingRoot ([IO.Path]::GetFileNameWithoutExtension($Project))
    & dotnet publish $Project `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $projectOutput `
        --property:PublishSingleFile=true `
        --property:IncludeNativeLibrariesForSelfExtract=true `
        --property:EnableCompressionInSingleFile=true `
        --property:DebugType=None `
        --property:DebugSymbols=false `
        --property:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "The local-development publish failed for $Project with exit code $LASTEXITCODE."
    }

    $source = Join-Path $projectOutput $ExecutableName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-Item -LiteralPath $source).Length -le 0) {
        throw "The local-development publish omitted $ExecutableName."
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $package $ExecutableName)
}

Assert-ChildPath -Path $target -Parent $artifacts
Assert-ChildPath -Path $stagingRoot -Parent $artifacts
New-Item -ItemType Directory -Path $package -Force | Out-Null
try {
    Invoke-SingleFilePublish `
        -Project (Join-Path $repository 'src\OpenBlade.DeploymentBroker\OpenBlade.DeploymentBroker.csproj') `
        -ExecutableName 'OpenBlade.DeploymentBroker.exe'
    Invoke-SingleFilePublish `
        -Project (Join-Path $repository 'src\OpenBlade.DeploymentClient\OpenBlade.DeploymentClient.csproj') `
        -ExecutableName 'OpenBlade.DeploymentClient.exe'
    Invoke-SingleFilePublish `
        -Project (Join-Path $repository 'src\OpenBlade.DevelopmentDeployment.Setup\OpenBlade.DevelopmentDeployment.Setup.csproj') `
        -ExecutableName 'OpenBlade.DevelopmentDeployment.Setup.exe'
    $toolProject = Join-Path $repository (
        'build\tools\OpenBlade.LocalDevDeployment.Tool\OpenBlade.LocalDevDeployment.Tool.csproj')
    $keyCommand = if ($CreateTpmKey) { 'create-key' } else { 'inspect-key' }
    $keyOutput = @(& dotnet run `
            --project $toolProject `
            --configuration $Configuration `
            --property:NuGetAudit=false `
            -- `
            $keyCommand `
            --key-name $TpmKeyName)
    if ($LASTEXITCODE -ne 0 -or $keyOutput.Count -eq 0) {
        throw "The TPM development key $keyCommand operation failed."
    }
    $key = $keyOutput[-1] | ConvertFrom-Json
    if ($key.keyId -cnotmatch '^[0-9a-f]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$key.subjectPublicKeyInfoBase64) -or
        -not [bool]$key.hardwareBacked) {
        throw 'The TPM development key inspection returned an invalid identity.'
    }
    $policy = [ordered]@{
        schemaVersion = 2
        enabled = $true
        policyId = 'openblade-local-dev-v1'
        signatureAlgorithm = 'ECDSA-P256-SHA256-P1363'
        signingDomain = 'OpenBlade-Local-Dev-Deployment-Manifest-v1'
        manifestFileName = 'OpenBlade-Local-Dev-Deployment.json'
        signatureFileName = 'OpenBlade-Local-Dev-Deployment.sig'
        installerFileName = 'OpenBlade-Setup.exe'
        productName = 'OpenBlade'
        bundleId = 'OSSBlade.OpenBlade'
        providerKey = 'OSSBlade.OpenBlade'
        upgradeCode = 'FF859CA9-D69F-4995-A2AD-69E540E45724'
        architecture = 'x64'
        minimumWindowsBuild = 26100
        quietInstallArguments = '/install /quiet /norestart'
        trustedDevelopmentKeys = @([ordered]@{
                keyId = [string]$key.keyId
                subjectPublicKeyInfoBase64 = [string]$key.subjectPublicKeyInfoBase64
            })
    }
    [IO.File]::WriteAllText(
        (Join-Path $package 'OpenBlade.LocalDevDeployment.json'),
        ($policy | ConvertTo-Json -Depth 4),
        [Text.UTF8Encoding]::new($false))

    $expected = @(
        'OpenBlade.DeploymentBroker.exe',
        'OpenBlade.DeploymentClient.exe',
        'OpenBlade.DevelopmentDeployment.Setup.exe',
        'OpenBlade.LocalDevDeployment.json') | Sort-Object
    $actual = @(Get-ChildItem -LiteralPath $package -File | Select-Object -ExpandProperty Name |
            Sort-Object)
    if (Compare-Object -ReferenceObject $expected -DifferenceObject $actual) {
        throw 'The local-development provisioner package does not contain its exact fixed file set.'
    }

    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    Move-Item -LiteralPath $package -Destination $target
    Write-Host "Local-development provisioner package: $target"
    Write-Host 'Running its install verb is the separate attended UAC step.'
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
