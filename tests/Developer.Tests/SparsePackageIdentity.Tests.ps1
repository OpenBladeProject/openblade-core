[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\SparsePackageIdentity.ps1')
. (Join-Path $repository 'build\PublishPayloadValidation.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    $caught = $null
    try {
        & $Action
    }
    catch {
        $caught = $_
    }
    Assert-True ($null -ne $caught) "$Message did not fail."
    Assert-True ($caught.Exception.Message -match $Pattern) `
        "$Message failed unexpectedly: $($caught.Exception.Message)"
}

Assert-True (
    (ConvertTo-OpenBladePackageVersion '0.1.0') -eq '0.1.0.0') `
    'Three-part product versions must become four-part MSIX versions.'
Assert-True (
    (ConvertTo-OpenBladePackageVersion '1.2.3.4') -eq '1.2.3.4') `
    'Four-part product versions must remain stable.'
Assert-ThrowsLike {
    ConvertTo-OpenBladePackageVersion '1.2.70000'
} 'between 0 and 65535' 'An out-of-range MSIX version'
Assert-ThrowsLike {
    Get-OpenBladeSigningCertificate -Thumbprint 'not-a-thumbprint'
} '40 hexadecimal' 'An invalid signing thumbprint'

$stringEku = [pscustomobject]@{
    ObjectId = '1.3.6.1.5.5.7.3.3'
}
Assert-True (
    (Get-OpenBladeEnhancedKeyUsageOid -Usage $stringEku) -eq
    '1.3.6.1.5.5.7.3.3') `
    'String-valued certificate-provider EKUs must be accepted.'

$oidEku = [pscustomobject]@{
    ObjectId = [Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')
}
Assert-True (
    (Get-OpenBladeEnhancedKeyUsageOid -Usage $oidEku) -eq
    '1.3.6.1.5.5.7.3.3') `
    'Oid-valued certificate-provider EKUs must be accepted.'

Assert-True (
    $null -eq (Get-OpenBladeEnhancedKeyUsageOid -Usage ([pscustomobject]@{}))) `
    'Malformed certificate-provider EKUs must be rejected.'

$identityRoot = Join-Path $repository 'build\identity'
$packageTemplatePath = Join-Path $identityRoot 'AppxManifest.xml.template'
$agentTemplatePath = Join-Path $identityRoot (
    'OpenBlade.SessionAgent.Identity.manifest.template')
[xml]$packageTemplate = Get-Content -LiteralPath $packageTemplatePath
[xml]$agentTemplate = Get-Content -LiteralPath $agentTemplatePath

$packageNamespaces = [Xml.XmlNamespaceManager]::new($packageTemplate.NameTable)
$packageNamespaces.AddNamespace(
    'f',
    'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$packageNamespaces.AddNamespace(
    'uap',
    'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$packageNamespaces.AddNamespace(
    'uap3',
    'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
$packageNamespaces.AddNamespace(
    'uap10',
    'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
$packageNamespaces.AddNamespace(
    'rescap',
    'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

$identity = $packageTemplate.SelectSingleNode('/f:Package/f:Identity', $packageNamespaces)
Assert-True (
    $identity.GetAttribute('Name') -eq 'OSSBlade.OpenBlade.DynamicLighting') `
    'The sparse package identity name changed.'
Assert-True ($identity.GetAttribute('Publisher') -eq '__PUBLISHER__') `
    'The sparse package publisher must be generated from the signing certificate.'
Assert-True ($identity.GetAttribute('Version') -eq '__PACKAGE_VERSION__') `
    'The sparse package version must be generated from the product version.'

$allowExternal = $packageTemplate.SelectSingleNode(
    '/f:Package/f:Properties/uap10:AllowExternalContent',
    $packageNamespaces)
Assert-True ($allowExternal.InnerText -eq 'true') `
    'The sparse package must explicitly allow external content.'

$application = $packageTemplate.SelectSingleNode(
    '/f:Package/f:Applications/f:Application',
    $packageNamespaces)
Assert-True ($application.GetAttribute('Id') -eq 'SessionAgent') `
    'The sparse identity must target the Session Agent application ID.'
Assert-True (
    $application.GetAttribute('Executable') -eq 'OpenBlade.SessionAgent.exe') `
    'The sparse identity must target the installed Session Agent executable.'
Assert-True (
    $application.GetAttribute(
        'RuntimeBehavior',
        'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -eq
        'packagedClassicApp') `
    'The external Session Agent must retain classic desktop runtime behavior.'
Assert-True (
    $application.GetAttribute(
        'TrustLevel',
        'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -eq
        'mediumIL') `
    'The Session Agent package identity must remain medium integrity.'

$visuals = $packageTemplate.SelectSingleNode(
    '/f:Package/f:Applications/f:Application/uap:VisualElements',
    $packageNamespaces)
Assert-True ($visuals.GetAttribute('AppListEntry') -eq 'none') `
    'The identity package must not create a duplicate app-list entry.'

$extension = $packageTemplate.SelectSingleNode(
    '/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension/uap3:AppExtension',
    $packageNamespaces)
Assert-True (
    $extension.GetAttribute('Name') -eq 'com.microsoft.windows.lighting') `
    'The Dynamic Lighting app extension name changed.'
Assert-True ($extension.GetAttribute('PublicFolder') -eq 'public') `
    'The Dynamic Lighting app extension must retain its public folder.'

$fullTrust = $packageTemplate.SelectSingleNode(
    '/f:Package/f:Capabilities/rescap:Capability',
    $packageNamespaces)
Assert-True ($fullTrust.GetAttribute('Name') -eq 'runFullTrust') `
    'The packaged classic Session Agent requires runFullTrust.'

$agentNamespaces = [Xml.XmlNamespaceManager]::new($agentTemplate.NameTable)
$agentNamespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
$agentNamespaces.AddNamespace('msix', 'urn:schemas-microsoft-com:msix.v1')
$msix = $agentTemplate.SelectSingleNode('/asm:assembly/msix:msix', $agentNamespaces)
Assert-True ($msix.GetAttribute('publisher') -eq '__PUBLISHER__') `
    'The Session Agent publisher must be generated from the signing certificate.'
Assert-True (
    $msix.GetAttribute('packageName') -eq $identity.GetAttribute('Name')) `
    'The executable and sparse package identity names differ.'
Assert-True (
    $msix.GetAttribute('applicationId') -eq $application.GetAttribute('Id')) `
    'The executable and sparse package application IDs differ.'

Add-Type -AssemblyName System.Drawing
$expectedAssetSizes = [ordered]@{
    'StoreLogo.png' = 50
    'Square44x44Logo.png' = 44
    'Square150x150Logo.png' = 150
}
foreach ($asset in $expectedAssetSizes.GetEnumerator()) {
    $image = [Drawing.Image]::FromFile(
        (Join-Path $identityRoot ('Assets\' + $asset.Key)))
    try {
        Assert-True (
            $image.Width -eq $asset.Value -and
            $image.Height -eq $asset.Value) `
            "Identity asset $($asset.Key) has the wrong dimensions."
    }
    finally {
        $image.Dispose()
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade.SparseIdentity.Tests.' + [Guid]::NewGuid().ToString('N'))
try {
    $payload = Join-Path $testRoot 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $renderedAgent = New-OpenBladeSessionAgentIdentityManifest `
        -TemplatePath $agentTemplatePath `
        -OutputPath (Join-Path $testRoot 'OpenBlade.SessionAgent.manifest') `
        -Publisher 'CN=OpenBlade & Test'
    [xml]$renderedAgentXml = Get-Content -LiteralPath $renderedAgent
    $renderedAgentNamespaces = [Xml.XmlNamespaceManager]::new(
        $renderedAgentXml.NameTable)
    $renderedAgentNamespaces.AddNamespace(
        'asm',
        'urn:schemas-microsoft-com:asm.v1')
    $renderedAgentNamespaces.AddNamespace(
        'msix',
        'urn:schemas-microsoft-com:msix.v1')
    $renderedMsix = $renderedAgentXml.SelectSingleNode(
        '/asm:assembly/msix:msix',
        $renderedAgentNamespaces)
    Assert-True (
        $renderedMsix.GetAttribute('publisher') -eq 'CN=OpenBlade & Test') `
        'The generated executable publisher was not XML escaped and restored exactly.'

    $artifacts = New-OpenBladeDynamicLightingPackage `
        -IdentityRoot $identityRoot `
        -StagingDirectory (Join-Path $testRoot 'package') `
        -PayloadDirectory $payload `
        -ProductVersion '0.1.0' `
        -Publisher 'CN=OpenBlade Test'
    Assert-True (
        (Split-Path -Parent $artifacts.PackagePath) -eq $payload) `
        'The sparse package was not emitted into the production payload.'
    Assert-True (
        (Split-Path -Parent $artifacts.DescriptorPath) -eq $payload) `
        'The sparse identity descriptor was not emitted beside the package.'
    Assert-DynamicLightingIdentityAuthoringArtifacts -Payload $payload
    Assert-ThrowsLike {
        Assert-DynamicLightingIdentityPayload -Payload $payload
    } 'must not be present in a production payload' `
        'A reviewed identity pair in a production payload'

    $descriptorJson = Get-Content -Raw -LiteralPath $artifacts.DescriptorPath
    $descriptor = $descriptorJson |
        ConvertFrom-Json
    Assert-True ($descriptor.schemaVersion -eq 1) `
        'The sparse identity descriptor schema changed.'
    Assert-True (
        $descriptor.packageName -eq $identity.GetAttribute('Name')) `
        'The sparse identity descriptor package name differs from the manifest.'
    Assert-True ($descriptor.publisher -eq 'CN=OpenBlade Test') `
        'The sparse identity descriptor publisher differs from the manifest.'
    Assert-True (
        $descriptor.applicationId -eq $application.GetAttribute('Id')) `
        'The sparse identity descriptor application ID differs from the manifest.'
    Assert-True ($descriptor.packageFile -eq 'OpenBlade.DynamicLighting.msix') `
        'The sparse identity descriptor package file changed.'

    $unpack = Join-Path $testRoot 'unpacked'
    $makeAppx = Resolve-OpenBladeMakeAppx
    & $makeAppx unpack /p $artifacts.PackagePath /d $unpack /o /nv | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx unpack failed with exit code $LASTEXITCODE."
    }
    [xml]$packedManifest = Get-Content -LiteralPath (
        Join-Path $unpack 'AppxManifest.xml')
    Assert-True (
        $packedManifest.Package.Identity.GetAttribute('Publisher') -eq
        'CN=OpenBlade Test') `
        'The packed identity publisher differs from the requested publisher.'
    Assert-True (
        $packedManifest.Package.Identity.GetAttribute('Version') -eq '0.1.0.0') `
        'The packed identity version differs from the product version.'

    [IO.File]::Delete($artifacts.DescriptorPath)
    Assert-ThrowsLike {
        Assert-DynamicLightingIdentityAuthoringArtifacts -Payload $payload
    } 'must either both be present or both be absent' `
        'A package without its identity descriptor'
    [IO.File]::WriteAllText(
        $artifacts.DescriptorPath,
        $descriptorJson,
        [Text.UTF8Encoding]::new($false))

    $mismatched = $descriptorJson | ConvertFrom-Json
    $mismatched.publisher = 'CN=Different Publisher'
    [IO.File]::WriteAllText(
        $artifacts.DescriptorPath,
        ($mismatched | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
    Assert-ThrowsLike {
        Assert-DynamicLightingIdentityAuthoringArtifacts -Payload $payload
    } 'differs from its reviewed descriptor' `
        'A package with a mismatched identity descriptor'
    [IO.File]::WriteAllText(
        $artifacts.DescriptorPath,
        $descriptorJson,
        [Text.UTF8Encoding]::new($false))

    Assert-ThrowsLike {
        New-OpenBladeDynamicLightingPackage `
            -IdentityRoot $identityRoot `
            -StagingDirectory (Join-Path $testRoot 'package') `
            -PayloadDirectory $payload `
            -ProductVersion '0.1.0' `
            -Publisher 'CN=OpenBlade Test'
    } 'Refusing to overwrite' 'A repeated identity package generation'

    $sessionAgentProject = Join-Path $repository (
        'src\OpenBlade.SessionAgent\OpenBlade.SessionAgent.csproj')
    $publishArguments = @(
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--property:NuGetAudit=false',
        '--property:PublishTrimmed=false',
        '--property:DebugType=None',
        '--property:DebugSymbols=false',
        '--no-restore'
    )
    $identityOutput = Join-Path $testRoot 'identity-agent'
    & dotnet publish $sessionAgentProject @publishArguments `
        "--property:OpenBladeSessionAgentManifestSource=$renderedAgent" `
        --output $identityOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Identity-enabled Session Agent publish failed with exit code $LASTEXITCODE."
    }

    # Reuse the same obj tree deliberately. This is the adversarial order that
    # must not leak an earlier identity claim into an unsigned build.
    $unsignedOutput = Join-Path $testRoot 'unsigned-agent'
    & dotnet publish $sessionAgentProject @publishArguments `
        --output $unsignedOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Unsigned Session Agent publish failed with exit code $LASTEXITCODE."
    }

    $manifestTool = Join-Path (Split-Path -Parent $makeAppx) 'mt.exe'
    Assert-True (Test-Path -LiteralPath $manifestTool -PathType Leaf) `
        'The Windows SDK manifest extraction tool is missing.'
    $identityExtract = Join-Path $testRoot 'identity-agent.manifest'
    $unsignedExtract = Join-Path $testRoot 'unsigned-agent.manifest'
    & $manifestTool `
        "-inputresource:$(Join-Path $identityOutput 'OpenBlade.SessionAgent.exe');#1" `
        "-out:$identityExtract"
    if ($LASTEXITCODE -ne 0) {
        throw "Identity-enabled manifest extraction failed with exit code $LASTEXITCODE."
    }
    & $manifestTool `
        "-inputresource:$(Join-Path $unsignedOutput 'OpenBlade.SessionAgent.exe');#1" `
        "-out:$unsignedExtract"
    if ($LASTEXITCODE -ne 0) {
        throw "Unsigned manifest extraction failed with exit code $LASTEXITCODE."
    }
    $identityManifestText = Get-Content -Raw -LiteralPath $identityExtract
    $unsignedManifestText = Get-Content -Raw -LiteralPath $unsignedExtract
    Assert-True (
        $identityManifestText -match 'urn:schemas-microsoft-com:msix.v1') `
        'The identity-enabled Session Agent lost its MSIX claim.'
    Assert-True (
        $unsignedManifestText -notmatch 'urn:schemas-microsoft-com:msix.v1') `
        'The unsigned Session Agent retained a stale MSIX identity claim.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'Developer sparse package identity tests passed.'
