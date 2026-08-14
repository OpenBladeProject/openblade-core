[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ArchivePath,
    [string]$PnPUtil = (Join-Path $env:WINDIR 'System32\pnputil.exe')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:PackageName = 'Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76'
$script:ExpectedSignerSubjectPrefix =
    'CN=Microsoft Windows Hardware Compatibility Publisher,'
$script:Packages = @(
    [ordered]@{
        Directory = 'rzdevu_02e0_dkm'
        Inf = 'rzdevu_02e0_dkm.inf'
        InfSha256 = 'C9831A19FE081F7AFA403E70116AB35762A3D4E339C4A7724CC6E64A3B035173'
        Catalog = 'rzdev_02e0_dkm.cat'
        CatalogSha256 = '2FB8F3A324D63590DBECE9C42D71E0B6498DA9BDECCB10045824056ED1EE4FFF'
        Binary = 'RzDev_02e0.sys'
        BinarySha256 = 'BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787'
        SignerThumbprint = '32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21'
        Role = 'Physical USB lower filter and virtual-device enumerator'
    },
    [ordered]@{
        Directory = 'rzdevu_02e0_vcon'
        Inf = 'rzdevu_02e0_vcon.inf'
        InfSha256 = 'BC3E0BCA22643930B25D4CF649144E89CFED0825493AA385931FF9F542FF3613'
        Catalog = 'rzdev_02e0_vcon.cat'
        CatalogSha256 = '0C3841B570DBC80819B44EB6027D3611FC2B39F063A612910F58F60EC99F0E21'
        Binary = 'RzDev_02e0.sys'
        BinarySha256 = 'BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787'
        SignerThumbprint = '32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21'
        Role = 'Virtual Consumer Control device function driver'
    },
    [ordered]@{
        Directory = 'rzcommonu'
        Inf = 'rzcommonu.inf'
        InfSha256 = 'DD3C8D65C7C3CCF2093FE70FB21FB8DC724E837880566BAE629B3104A5B1B37B'
        Catalog = 'rzcommon.cat'
        CatalogSha256 = '1DFBA6428F727EA414F0FB5E938EE76063B1193A6CC12895396C9B89486C6F6E'
        Binary = 'RzCommon.sys'
        BinarySha256 = '64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59'
        SignerThumbprint = 'FAC666005546D6BE881A31C1267717879401A950'
        Role = 'Razer control child and typed control interface'
    },
    [ordered]@{
        Directory = 'rzdevu_02e0_kbd'
        Inf = 'rzdevu_02e0_kbd.inf'
        InfSha256 = 'F3222D8A260DA3BFC87C19080C080D675E90D1F1EB7D1793A6148CBE3E90F5F3'
        Catalog = 'rzdev_02e0_kbd.cat'
        CatalogSha256 = '75F200B0643A3F79E29646C2DD98A6FDA419F5D2608F691AE8C6AB587DD6A108'
        Binary = 'RzDev_02e0.sys'
        BinarySha256 = 'BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787'
        SignerThumbprint = '32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21'
        Role = 'Physical keyboard collection upper filter'
    },
    [ordered]@{
        Directory = 'rzdevu_02e0_mou'
        Inf = 'rzdevu_02e0_mou.inf'
        InfSha256 = '05AC3D6E2BC6B84B6F255CBEAD573156FC6F1C0CF7B44924D873572F944B649B'
        Catalog = 'rzdev_02e0_mou.cat'
        CatalogSha256 = '8C3A7AD5C68B9ACAFB36B35DD8E20A18B4B188E9594FFC7626A406FC75E92FC1'
        Binary = 'RzDev_02e0.sys'
        BinarySha256 = 'BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787'
        SignerThumbprint = '32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21'
        Role = 'Physical mouse collection upper filter and control-device enumerator'
    }
)

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-ExactHash {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )

    $actual = Get-FileSha256 -Path $Path
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "The file '$Path' has SHA-256 '$actual'; expected '$Expected'."
    }
}

function Assert-ExactMicrosoftSignature {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedThumbprint
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) -or
        ($null -eq $signature.SignerCertificate) -or
        (-not $signature.SignerCertificate.Subject.StartsWith(
            $script:ExpectedSignerSubjectPrefix,
            [StringComparison]::Ordinal)) -or
        (-not [string]::Equals(
            $signature.SignerCertificate.Thumbprint,
            $ExpectedThumbprint,
            [StringComparison]::OrdinalIgnoreCase))) {
        throw "The file '$Path' does not have the exact admitted Microsoft hardware signature."
    }
}

function Find-ExactPublishedInf {
    param([Parameter(Mandatory)][Collections.IDictionary]$Package)

    $matches = @(
        Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'INF') -Filter 'oem*.inf' -File |
            Where-Object {
                [string]::Equals(
                    (Get-FileSha256 -Path $_.FullName),
                    $Package.InfSha256,
                    [StringComparison]::Ordinal)
            })
    if ($matches.Count -ne 1) {
        throw "Expected one installed published INF matching '$($Package.Inf)', found $($matches.Count)."
    }

    return $matches[0]
}

function Assert-ExactPackageFileSet {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string[]]$ExpectedFiles,
        [Parameter(Mandatory)][string]$OriginalInf
    )

    $entries = @(Get-ChildItem -LiteralPath $PackageRoot -Force)
    $unsafeEntries = @(
        $entries | Where-Object {
            $_.PSIsContainer -or
            (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        })
    $actualFiles = @(
        $entries | Where-Object { -not $_.PSIsContainer } |
            ForEach-Object { $_.Name })
    $unexpectedFiles = @($actualFiles | Where-Object { $_ -notin $ExpectedFiles })
    $missingFiles = @($ExpectedFiles | Where-Object { $_ -notin $actualFiles })
    if (($unsafeEntries.Count -ne 0) -or
        ($unexpectedFiles.Count -ne 0) -or
        ($missingFiles.Count -ne 0)) {
        throw "The exported '$OriginalInf' package does not contain its exact three-file set."
    }
}

function Export-ExactPackage {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Package,
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$ResolvedPnPUtil
    )

    $publishedInf = Find-ExactPublishedInf -Package $Package
    New-Item -ItemType Directory -Path $PackageRoot -ErrorAction Stop | Out-Null
    & $ResolvedPnPUtil /export-driver $publishedInf.Name $PackageRoot
    if ($LASTEXITCODE -ne 0) {
        throw "PnPUtil could not export '$($Package.Inf)' (exit code $LASTEXITCODE)."
    }

    Assert-ExactPackageFileSet `
        -PackageRoot $PackageRoot `
        -ExpectedFiles @($Package.Inf, $Package.Catalog, $Package.Binary) `
        -OriginalInf $Package.Inf

    foreach ($file in @(
            @{ Name = $Package.Inf; Hash = $Package.InfSha256 },
            @{ Name = $Package.Catalog; Hash = $Package.CatalogSha256 },
            @{ Name = $Package.Binary; Hash = $Package.BinarySha256 })) {
        $path = Join-Path $PackageRoot $file.Name
        Assert-ExactHash -Path $path -Expected $file.Hash
        Assert-ExactMicrosoftSignature `
            -Path $path `
            -ExpectedThumbprint $Package.SignerThumbprint
    }
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationPath,
        [string]$ArchiveRootName = (Split-Path -Leaf $SourceDirectory)
    )

    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open(
        $DestinationPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File |
                Sort-Object FullName) {
                $relative = $file.FullName.Substring($SourceDirectory.Length + 1)
                $entryName = ($ArchiveRootName + '/' + $relative).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $entryName,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    2026, 1, 28, 0, 0, 0, [TimeSpan]::Zero)
                $input = $file.OpenRead()
                try {
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
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

function Write-RazerPackageContents {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$ResolvedPnPUtil
    )

    foreach ($package in $script:Packages) {
        Export-ExactPackage `
            -Package $package `
            -PackageRoot (Join-Path $PackageRoot $package.Directory) `
            -ResolvedPnPUtil $ResolvedPnPUtil
    }

    $manifestPackages = @(
        foreach ($package in $script:Packages) {
            [ordered]@{
                directory = $package.Directory
                originalInf = $package.Inf
                role = $package.Role
                catalog = $package.Catalog
                binary = $package.Binary
                signerThumbprint = $package.SignerThumbprint
                files = @(
                    [ordered]@{
                        path = "$($package.Directory)/$($package.Inf)"
                        sha256 = $package.InfSha256
                    },
                    [ordered]@{
                        path = "$($package.Directory)/$($package.Catalog)"
                        sha256 = $package.CatalogSha256
                    },
                    [ordered]@{
                        path = "$($package.Directory)/$($package.Binary)"
                        sha256 = $package.BinarySha256
                    }
                )
            }
        })
    $manifest = [ordered]@{
        schemaVersion = 1
        package = $script:PackageName
        provider = 'Razer Inc'
        supportedModel = 'Razer Blade 16 RZ09-0581'
        usbIdentity = '1532:02E0'
        architecture = 'amd64'
        rzDevFileVersion = '1.0.0.78'
        rzCommonFileVersion = '1.0.0.76'
        packages = $manifestPackages
    }
    Write-Utf8NoBom `
        -Path (Join-Path $PackageRoot 'manifest.json') `
        -Content (($manifest | ConvertTo-Json -Depth 8) + "`n")

    $notice = @'
Razer driver package notice

This archive contains unmodified driver packages published by Razer Inc and
signed by Microsoft Windows Hardware Compatibility Publisher. Razer retains
all right, title, and interest in those files. The OpenBlade GPL license does
not apply to them and does not itself grant redistribution rights.

Anyone publishing this archive is responsible for retaining separate written
authorization from Razer that covers the exact files and distribution method.
OpenBlade is an independent project and this archive does not imply Razer
endorsement.
'@
    Write-Utf8NoBom `
        -Path (Join-Path $PackageRoot 'THIRD-PARTY-NOTICE.txt') `
        -Content ($notice.Trim() + "`n")

    $readme = @'
Razer 02E0 driver stack for OpenBlade validation

This package is an exact export of the five signed Razer driver packages found
on the hardware-validated Razer Blade 16 RZ09-0581 system. It contains no
Synapse application files, generated PNF cache, device instance IDs, serials,
or other machine-specific data.

The package is not yet an installer. Clean-machine package order, rollback,
reboot, repair, and removal have not been validated. Do not install it on a
different Blade model, including the RZ09-0528. See manifest.json for the exact
identity and hashes.
'@
    Write-Utf8NoBom `
        -Path (Join-Path $PackageRoot 'README.txt') `
        -Content ($readme.Trim() + "`n")

    $sumLines = @(
        Get-ChildItem -LiteralPath $PackageRoot -Recurse -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
            Sort-Object FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring($PackageRoot.Length + 1).Replace('\', '/')
                "$(Get-FileSha256 -Path $_.FullName)  $relative"
            })
    Write-Utf8NoBom `
        -Path (Join-Path $PackageRoot 'SHA256SUMS.txt') `
        -Content (($sumLines -join "`n") + "`n")
}

function Invoke-AtomicPackageBuild {
    param(
        [Parameter(Mandatory)][string]$FinalDirectory,
        [Parameter(Mandatory)][string]$FinalArchive,
        [Parameter(Mandatory)][scriptblock]$BuildAction
    )

    $directoryParent = Split-Path -Parent $FinalDirectory
    $archiveParent = Split-Path -Parent $FinalArchive
    New-Item -ItemType Directory -Path $directoryParent -Force -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path $archiveParent -Force -ErrorAction Stop | Out-Null

    $token = [Guid]::NewGuid().ToString('N')
    $stagingDirectory = Join-Path $directoryParent (
        ".$(Split-Path -Leaf $FinalDirectory).staging-$token")
    $stagingArchive = Join-Path $archiveParent (
        ".$(Split-Path -Leaf $FinalArchive).staging-$token.tmp")
    $publishedDirectory = $false
    $publishedArchive = $false
    try {
        New-Item -ItemType Directory -Path $stagingDirectory -ErrorAction Stop | Out-Null
        & $BuildAction $stagingDirectory $stagingArchive
        if (-not (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
            throw 'The package build did not produce its staging directory.'
        }
        if (-not (Test-Path -LiteralPath $stagingArchive -PathType Leaf)) {
            throw 'The package build did not produce its staging archive.'
        }

        [IO.Directory]::Move($stagingDirectory, $FinalDirectory)
        $publishedDirectory = $true
        [IO.File]::Move($stagingArchive, $FinalArchive)
        $publishedArchive = $true
    }
    catch {
        if (Test-Path -LiteralPath $stagingArchive) {
            Remove-Item -LiteralPath $stagingArchive -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $stagingDirectory) {
            Remove-Item `
                -LiteralPath $stagingDirectory `
                -Recurse `
                -Force `
                -ErrorAction SilentlyContinue
        }
        if ($publishedArchive -and (Test-Path -LiteralPath $FinalArchive)) {
            Remove-Item -LiteralPath $FinalArchive -Force -ErrorAction SilentlyContinue
        }
        if ($publishedDirectory -and (Test-Path -LiteralPath $FinalDirectory)) {
            Remove-Item `
                -LiteralPath $FinalDirectory `
                -Recurse `
                -Force `
                -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Test-PathInside {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Root
    )

    if ([string]::Equals($Candidate, $Root, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $prefix = $Root.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-RazerBrightnessDriverPackageExport {
    param(
        [Parameter(Mandatory)][string]$RequestedOutputDirectory,
        [string]$RequestedArchivePath,
        [Parameter(Mandatory)][string]$ResolvedPnPUtil
    )

    if (($PSVersionTable.PSEdition -ne 'Desktop') -or
        ($PSVersionTable.PSVersion -lt [version]'5.1')) {
        throw 'The exporter requires Windows PowerShell 5.1 for reproducible output.'
    }

    $resolvedOutput = [IO.Path]::GetFullPath($RequestedOutputDirectory)
    if (-not [string]::Equals(
            (Split-Path -Leaf $resolvedOutput),
            $script:PackageName,
            [StringComparison]::Ordinal)) {
        throw "The package directory name must be '$($script:PackageName)'."
    }
    if (Test-Path -LiteralPath $resolvedOutput) {
        throw "The output directory already exists: $resolvedOutput"
    }
    if (-not (Test-Path -LiteralPath $ResolvedPnPUtil -PathType Leaf)) {
        throw "PnPUtil is missing: $ResolvedPnPUtil"
    }

    if ([string]::IsNullOrWhiteSpace($RequestedArchivePath)) {
        $resolvedArchive = "$resolvedOutput.zip"
    }
    else {
        $resolvedArchive = [IO.Path]::GetFullPath($RequestedArchivePath)
    }
    if (Test-Path -LiteralPath $resolvedArchive) {
        throw "The archive already exists: $resolvedArchive"
    }
    if (Test-PathInside -Candidate $resolvedArchive -Root $resolvedOutput) {
        throw 'The archive must be outside the package directory.'
    }

    $repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    if ((Test-PathInside -Candidate $resolvedOutput -Root $repository) -or
        (Test-PathInside -Candidate $resolvedArchive -Root $repository)) {
        throw 'The proprietary package and archive must be outside the repository.'
    }

    Invoke-AtomicPackageBuild `
        -FinalDirectory $resolvedOutput `
        -FinalArchive $resolvedArchive `
        -BuildAction {
            param($stagingDirectory, $stagingArchive)

            Write-RazerPackageContents `
                -PackageRoot $stagingDirectory `
                -ResolvedPnPUtil $ResolvedPnPUtil
            New-DeterministicZip `
                -SourceDirectory $stagingDirectory `
                -DestinationPath $stagingArchive `
                -ArchiveRootName $script:PackageName
        }

    return [pscustomobject]@{
        PackageDirectory = $resolvedOutput
        Archive = $resolvedArchive
        ArchiveSha256 = Get-FileSha256 -Path $resolvedArchive
        PackageCount = $script:Packages.Count
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        throw 'OutputDirectory is required.'
    }
    Invoke-RazerBrightnessDriverPackageExport `
        -RequestedOutputDirectory $OutputDirectory `
        -RequestedArchivePath $ArchivePath `
        -ResolvedPnPUtil $PnPUtil
}
