[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$PreSignedPackage,
    [string]$SigningThumbprint,
    [string]$SignTool,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Resolve-SignToolPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "The requested SignTool executable is missing: $resolved"
        }
        return $resolved
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $sdkRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $sdkRoot -Filter 'signtool.exe' `
            -File -Recurse -ErrorAction Stop |
            Where-Object {
                $_.Directory.Name.Equals('x64', [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw 'SignTool.exe is required to verify or sign the brightness HID package.'
}

$project = Join-Path $Repository 'src\OpenBlade.BrightnessHid\OpenBlade.BrightnessHid.vcxproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "The OpenBlade brightness HID driver project is missing: $project"
}

$packageNames = @(
    'OpenBlade.BrightnessHid.inf',
    'OpenBlade.BrightnessHid.cat',
    'OpenBlade.BrightnessHid.dll'
)
if (-not [string]::IsNullOrWhiteSpace($PreSignedPackage)) {
    $signedRoot = [IO.Path]::GetFullPath($PreSignedPackage)
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    foreach ($name in $packageNames) {
        $source = Join-Path $signedRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The pre-signed brightness HID package is missing $name."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $Output $name)
    }
    $catalogSignature = Get-AuthenticodeSignature -LiteralPath (
        Join-Path $Output 'OpenBlade.BrightnessHid.cat')
    if ($catalogSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $catalogSignature.SignerCertificate -or
        $catalogSignature.SignerCertificate.Subject.IndexOf(
            'Microsoft Windows Hardware Compatibility Publisher',
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'The brightness HID catalog must carry a valid Microsoft Hardware Dev Center signature.'
    }
    $catalogVerifier = Resolve-SignToolPath -RequestedPath $SignTool
    foreach ($member in @(
            'OpenBlade.BrightnessHid.inf',
            'OpenBlade.BrightnessHid.dll')) {
        & $catalogVerifier verify /kp /c (
            Join-Path $Output 'OpenBlade.BrightnessHid.cat') (
            Join-Path $Output $member)
        if ($LASTEXITCODE -ne 0) {
            throw "The Microsoft-signed brightness HID catalog does not verify exact member $member."
        }
    }
}
else {
    $msbuild = Get-Command 'msbuild.exe' -ErrorAction SilentlyContinue
    if ($null -eq $msbuild) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $resolved = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($resolved)) {
                $msbuild = Get-Item -LiteralPath $resolved
            }
        }
    }
    if ($null -eq $msbuild) {
        throw 'The OpenBlade brightness HID driver requires Visual Studio MSBuild and the Windows Driver Kit. Install the pinned driver build prerequisites before publishing.'
    }

    $driverBuildRoot = Join-Path $Output '.driver-build'
    if (Test-Path -LiteralPath $driverBuildRoot) {
        Remove-Item -LiteralPath $driverBuildRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $driverBuildRoot -Force | Out-Null

    & $msbuild.FullName $project `
        /restore `
        /m:1 `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:BaseIntermediateOutputPath="$driverBuildRoot\obj\" `
        /p:OutDir="$driverBuildRoot\out\"
    if ($LASTEXITCODE -ne 0) {
        throw "OpenBlade brightness HID driver build failed with exit code $LASTEXITCODE. Verify that Visual Studio C++ and the pinned Microsoft.Windows.WDK.x64 package are available."
    }

    $packageFiles = @{}
    foreach ($name in $packageNames) {
        $matches = @(Get-ChildItem -LiteralPath $driverBuildRoot -File -Recurse |
            Where-Object { $_.Name -ceq $name })
        if ($matches.Count -ne 1) {
            throw "The driver build must produce exactly one $name; found $($matches.Count)."
        }
        $packageFiles[$name] = $matches[0].FullName
    }

    $signingEnabled = -not [string]::IsNullOrWhiteSpace($SigningThumbprint)
    if ($signingEnabled) {
        $resolvedSignTool = Resolve-SignToolPath -RequestedPath $SignTool
        foreach ($name in @('OpenBlade.BrightnessHid.dll')) {
            & $resolvedSignTool sign /sha1 $SigningThumbprint /fd SHA256 `
                /tr $TimestampUrl /td SHA256 $packageFiles[$name]
            if ($LASTEXITCODE -ne 0) {
                throw "Driver signing failed with exit code $LASTEXITCODE for $name"
            }
        }
    }

    $packageDirectory = Split-Path -Parent $packageFiles['OpenBlade.BrightnessHid.inf']
    $wdkPropertyOutput = @(& $msbuild.FullName $project `
        /nologo `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /getProperty:PkgMicrosoft_Windows_WDK_x64)
    if ($LASTEXITCODE -ne 0) {
        throw 'MSBuild could not resolve the pinned Microsoft.Windows.WDK.x64 package root.'
    }
    $wdkRoot = $wdkPropertyOutput |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($wdkRoot) -or
        -not (Test-Path -LiteralPath $wdkRoot -PathType Container)) {
        throw 'The pinned Microsoft.Windows.WDK.x64 package root is unavailable after restore.'
    }
    $inf2CatMatches = @(Get-ChildItem -LiteralPath $wdkRoot -Filter 'Inf2Cat.exe' `
        -File -Recurse -ErrorAction Stop)
    if ($inf2CatMatches.Count -ne 1) {
        throw "The pinned Windows Driver Kit package must contain exactly one Inf2Cat.exe; found $($inf2CatMatches.Count)."
    }
    $inf2Cat = $inf2CatMatches[0]
    if ($null -eq $inf2Cat) {
        throw 'Inf2Cat.exe from the pinned Windows Driver Kit is required to catalog the exact signed driver DLL.'
    }
    Remove-Item -LiteralPath $packageFiles['OpenBlade.BrightnessHid.cat'] -Force `
        -ErrorAction SilentlyContinue
    & $inf2Cat.FullName /driver:$packageDirectory /os:10_GE_X64
    if ($LASTEXITCODE -ne 0) {
        throw "Inf2Cat failed with exit code $LASTEXITCODE for the OpenBlade brightness HID package."
    }
    $packageFiles['OpenBlade.BrightnessHid.cat'] = Join-Path `
        $packageDirectory `
        'OpenBlade.BrightnessHid.cat'
    if ($signingEnabled) {
        & $resolvedSignTool sign /sha1 $SigningThumbprint /fd SHA256 `
            /tr $TimestampUrl /td SHA256 $packageFiles['OpenBlade.BrightnessHid.cat']
        if ($LASTEXITCODE -ne 0) {
            throw "Driver catalog signing failed with exit code $LASTEXITCODE"
        }
    }

    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    foreach ($name in $packageNames) {
        Copy-Item -LiteralPath $packageFiles[$name] -Destination (Join-Path $Output $name)
    }

    if ($signingEnabled) {
        foreach ($name in @('OpenBlade.BrightnessHid.dll', 'OpenBlade.BrightnessHid.cat')) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Output $name)
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                throw "The signed driver package file $name has signature status $($signature.Status)."
            }
        }
    }

    Remove-Item -LiteralPath $driverBuildRoot -Recurse -Force
}

$inf = Get-Content -Raw -LiteralPath (Join-Path $Output 'OpenBlade.BrightnessHid.inf')
foreach ($required in @(
        'ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID',
        'CatalogFile=OpenBlade.BrightnessHid.cat',
        'OpenBlade.BrightnessHid.dll')) {
    if ($inf.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The OpenBlade brightness HID INF is missing required contract '$required'."
    }
}
$buildProps = [xml](Get-Content -Raw -LiteralPath (
    Join-Path $Repository 'Directory.Build.props'))
$productVersion = [string]$buildProps.Project.PropertyGroup.OpenBladeVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or
    $inf -notmatch "(?m)^DriverVer=\d{2}/\d{2}/\d{4},$([regex]::Escape($productVersion))\.0\s*$") {
    throw "The OpenBlade brightness HID package does not match product version $productVersion.0."
}
