Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:OpenBladeDynamicLightingPackageName =
    'OSSBlade.OpenBlade.DynamicLighting'
$script:OpenBladeDynamicLightingApplicationId = 'SessionAgent'
$script:OpenBladeDynamicLightingPackageFile =
    'OpenBlade.DynamicLighting.msix'
$script:OpenBladeDynamicLightingDescriptorFile =
    'OpenBlade.DynamicLighting.identity.json'

function ConvertTo-OpenBladePackageVersion {
    param([Parameter(Mandatory)][string]$ProductVersion)

    try {
        $parsed = [Version]$ProductVersion
    }
    catch {
        throw "OpenBlade product version is not valid for MSIX: $ProductVersion"
    }

    $parts = @(
        $parsed.Major,
        $parsed.Minor,
        $(if ($parsed.Build -lt 0) { 0 } else { $parsed.Build }),
        $(if ($parsed.Revision -lt 0) { 0 } else { $parsed.Revision })
    )
    if (@($parts | Where-Object { $_ -lt 0 -or $_ -gt 65535 }).Count -ne 0) {
        throw "OpenBlade MSIX version components must be between 0 and 65535: $ProductVersion"
    }

    return ($parts -join '.')
}

function Get-OpenBladeSigningCertificate {
    param([Parameter(Mandatory)][string]$Thumbprint)

    $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw 'The OpenBlade signing certificate thumbprint must contain exactly 40 hexadecimal characters.'
    }

    $matches = @()
    foreach ($store in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $candidate = Join-Path $store $normalized
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $matches += Get-Item -LiteralPath $candidate
        }
    }
    if ($matches.Count -eq 0) {
        throw "The OpenBlade signing certificate was not found: $normalized"
    }

    $certificate = $matches[0]
    if (-not $certificate.HasPrivateKey) {
        throw 'The OpenBlade signing certificate does not have an accessible private key.'
    }
    if ($certificate.NotBefore -gt [DateTime]::Now -or
        $certificate.NotAfter -le [DateTime]::Now) {
        throw 'The OpenBlade signing certificate is not currently valid.'
    }
    $codeSigningEku = @($certificate.EnhancedKeyUsageList | Where-Object {
        $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3'
    })
    if ($codeSigningEku.Count -eq 0) {
        throw 'The OpenBlade signing certificate is not valid for code signing.'
    }
    if ([string]::IsNullOrWhiteSpace($certificate.Subject)) {
        throw 'The OpenBlade signing certificate has no publisher subject.'
    }

    return $certificate
}

function Resolve-OpenBladeMakeAppx {
    param([string]$MakeAppx)

    if (-not [string]::IsNullOrWhiteSpace($MakeAppx)) {
        $resolved = [IO.Path]::GetFullPath($MakeAppx)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "The requested MakeAppx executable is missing: $resolved"
        }
        return $resolved
    }

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidates = @(Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction Stop |
        Where-Object { $_.Name -as [Version] } |
        Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count -eq 0) {
        throw 'MakeAppx.exe was not found in the installed Windows SDK.'
    }

    return $candidates[0]
}

function New-OpenBladeSessionAgentIdentityManifest {
    param(
        [Parameter(Mandatory)][string]$TemplatePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$Publisher
    )

    if (Test-Path -LiteralPath $OutputPath) {
        throw "Refusing to overwrite an existing generated identity manifest: $OutputPath"
    }
    $template = [IO.File]::ReadAllText($TemplatePath)
    if ($template.IndexOf('__PUBLISHER__', [StringComparison]::Ordinal) -lt 0) {
        throw "The Session Agent identity manifest template has no publisher placeholder: $TemplatePath"
    }
    $escapedPublisher = [Security.SecurityElement]::Escape($Publisher)
    $rendered = $template.Replace('__PUBLISHER__', $escapedPublisher)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $OutputPath)) | Out-Null
    [IO.File]::WriteAllText(
        $OutputPath,
        $rendered,
        [Text.UTF8Encoding]::new($false))
    return $OutputPath
}

function New-OpenBladeDynamicLightingPackage {
    param(
        [Parameter(Mandatory)][string]$IdentityRoot,
        [Parameter(Mandatory)][string]$StagingDirectory,
        [Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$ProductVersion,
        [Parameter(Mandatory)][string]$Publisher,
        [string]$MakeAppx
    )

    if (Test-Path -LiteralPath $StagingDirectory) {
        throw "Refusing to overwrite an existing Dynamic Lighting identity path: $StagingDirectory"
    }
    if (-not (Test-Path -LiteralPath $PayloadDirectory -PathType Container)) {
        throw "The Dynamic Lighting payload directory does not exist: $PayloadDirectory"
    }
    [IO.Directory]::CreateDirectory($StagingDirectory) | Out-Null

    $templatePath = Join-Path $IdentityRoot 'AppxManifest.xml.template'
    $template = [IO.File]::ReadAllText($templatePath)
    foreach ($placeholder in @('__PUBLISHER__', '__PACKAGE_VERSION__')) {
        if ($template.IndexOf($placeholder, [StringComparison]::Ordinal) -lt 0) {
            throw "The Dynamic Lighting package template is missing $placeholder."
        }
    }
    $manifest = $template.Replace(
        '__PUBLISHER__',
        [Security.SecurityElement]::Escape($Publisher))
    $manifest = $manifest.Replace(
        '__PACKAGE_VERSION__',
        (ConvertTo-OpenBladePackageVersion $ProductVersion))
    [IO.File]::WriteAllText(
        (Join-Path $StagingDirectory 'AppxManifest.xml'),
        $manifest,
        [Text.UTF8Encoding]::new($false))

    $assetSource = Join-Path $IdentityRoot 'Assets'
    $assetDestination = Join-Path $StagingDirectory 'Assets'
    [IO.Directory]::CreateDirectory($assetDestination) | Out-Null
    foreach ($asset in @(
            'StoreLogo.png',
            'Square44x44Logo.png',
            'Square150x150Logo.png')) {
        $source = Join-Path $assetSource $asset
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The Dynamic Lighting identity asset is missing: $source"
        }
        [IO.File]::Copy($source, (Join-Path $assetDestination $asset))
    }

    $publicDirectory = Join-Path $StagingDirectory 'public'
    [IO.Directory]::CreateDirectory($publicDirectory) | Out-Null
    $publicMetadata = [ordered]@{
        schemaVersion = 1
        extension = 'com.microsoft.windows.lighting'
        applicationId = $script:OpenBladeDynamicLightingApplicationId
    }
    [IO.File]::WriteAllText(
        (Join-Path $publicDirectory 'OpenBlade.DynamicLighting.json'),
        ($publicMetadata | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))

    $packagePath = Join-Path $PayloadDirectory $script:OpenBladeDynamicLightingPackageFile
    $descriptorPath = Join-Path $PayloadDirectory $script:OpenBladeDynamicLightingDescriptorFile
    foreach ($path in @($packagePath, $descriptorPath)) {
        if (Test-Path -LiteralPath $path) {
            throw "Refusing to overwrite an existing Dynamic Lighting identity artifact: $path"
        }
    }
    $makeAppxPath = Resolve-OpenBladeMakeAppx -MakeAppx $MakeAppx
    # Sparse packages deliberately reference an executable supplied by the
    # registered external location, so MakeAppx semantic file-existence
    # validation must be disabled. The checked-in authoring tests validate the
    # manifest contract independently.
    & $makeAppxPath pack /d $StagingDirectory /p $packagePath /o /nv | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "MakeAppx did not create the Dynamic Lighting identity package: $packagePath"
    }

    $descriptor = [ordered]@{
        schemaVersion = 1
        packageName = $script:OpenBladeDynamicLightingPackageName
        publisher = $Publisher
        applicationId = $script:OpenBladeDynamicLightingApplicationId
        packageFile = $script:OpenBladeDynamicLightingPackageFile
        externalLocation = 'OpenBlade install directory'
    }
    [IO.File]::WriteAllText(
        $descriptorPath,
        ($descriptor | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))

    return [pscustomobject]@{
        PackagePath = $packagePath
        DescriptorPath = $descriptorPath
        Publisher = $Publisher
    }
}
