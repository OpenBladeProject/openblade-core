#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceRevision,
    [Parameter(Mandatory)][string]$ScratchDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $IsWindows) { throw 'This fixture requires Windows.' }

$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/../../..").Path
$commit = & git -C $repo rev-parse --verify "$SourceRevision^{commit}"
if ($commit -cne $SourceRevision) { throw 'SourceRevision must be the full commit ID.' }
$installerProject = Join-Path $repo 'build/installer/OpenBlade.Installer.wixproj'
[xml]$project = Get-Content -Raw -LiteralPath $installerProject
$eula = [string]$project.Project.PropertyGroup.AcceptEula
if ($project.Project.Sdk -cne 'WixToolset.Sdk/7.0.0' -or $eula -cne 'wix7') {
    throw 'Expected the checked-in WiX 7.0.0 SDK and wix7 EULA option.'
}
. (Join-Path $repo 'build/InstallerValidation.ps1')
$wix = Get-WixCommandAssembly -InstallerProject $installerProject

$scratch = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ScratchDirectory)
New-Item -ItemType Directory -Path $scratch | Out-Null
$source = Join-Path $scratch 'source'
$archive = Join-Path $scratch 'source.zip'
$cargoTarget = Join-Path $scratch 'cargo'
$staging = Join-Path $scratch 'staging'
$encode = Join-Path $scratch 'encoder'
$buildIntermediate = Join-Path $scratch 'wix-build'
$extractIntermediate = Join-Path $scratch 'wix-extract'
$ba = Join-Path $scratch 'ba'
$extracted = Join-Path $scratch 'extracted'
$bundle = Join-Path $scratch 'OpenBlade.NativeRoles.OfflineFixture.exe'
New-Item -ItemType Directory -Path $staging,$buildIntermediate,$extractIntermediate,$ba,$extracted | Out-Null

# Build from immutable Git inputs so local edits and prior Cargo outputs cannot enter this pair.
# The two candidate bins embed the managed contract and model inventory through include_str!.
& git -C $repo archive --format=zip --output=$archive $SourceRevision -- `
    Directory.Build.props rust tools/openblade-windows-host `
    tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json `
    tests/OpenBlade.Protocol.Tests/Fixtures/windows-rust-model-evidence-inventory.json `
    tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json `
    tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-profile.json
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $source)
[xml]$props = Get-Content -Raw -LiteralPath (Join-Path $source 'Directory.Build.props')
$version = "$($props.SelectSingleNode('/Project/PropertyGroup/OpenBladeVersion').InnerText).0"
$service = 'openblade-windows-service-candidate'
$agent = 'openblade-windows-agent-candidate'
$target = 'x86_64-pc-windows-msvc'
Push-Location -LiteralPath $source
try {
    & cargo +1.99.1 build --offline --locked --release --target $target --target-dir $cargoTarget `
        --manifest-path tools/openblade-windows-host/Cargo.toml `
        --features service-metadata-only-v1,native-agent-entry-compile-check --bin $service --bin $agent
}
finally { Pop-Location }
foreach ($role in $service,$agent) {
    [IO.File]::Copy("$cargoTarget/$target/release/$role.exe", "$staging/$role.exe")
}

# Only the fixture project links the existing test-only encoder. Restore has no feeds.
$nugetConfig = Join-Path $scratch 'NuGet.Config'
[IO.File]::WriteAllText($nugetConfig, '<configuration><packageSources><clear /></packageSources></configuration>')
& dotnet build "$PSScriptRoot/InventoryWriter.csproj" -c Release --nologo `
    --artifacts-path "$scratch/dotnet" --output $encode --configfile $nugetConfig -p:NuGetAudit=false | Out-Host
& dotnet "$encode/InventoryWriter.dll" write $SourceRevision $version $staging

# Burn needs a BA and a primary chain payload. Both placeholders are plain text.
[IO.File]::WriteAllText("$staging/FixtureBootstrapper.exe", 'Inert fixture bootstrapper placeholder')
[IO.File]::WriteAllText("$staging/FixtureSetup.exe", 'Inert fixture chain package placeholder')
& dotnet $wix -acceptEula $eula build -arch x64 -d "FixtureVersion=$version" -b $staging `
    -intermediateFolder $buildIntermediate -o $bundle "$PSScriptRoot/Bundle.wxs" | Out-Host

# This CLI opens by path. Hold a read-sharing guard for the locally built fixture.
$guard = [IO.File]::Open($bundle, 'Open', 'Read', 'Read')
try {
    $bundleHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($guard))
    & dotnet $wix -acceptEula $eula burn extract $bundle -out $extracted -outba $ba `
        -intermediateFolder $extractIntermediate | Out-Host
}
finally { $guard.Dispose() }

[xml]$manifest = Get-Content -Raw -LiteralPath "$ba/manifest.xml"
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('b', 'http://wixtoolset.org/schemas/v4/2008/Burn')
$burn = $manifest.DocumentElement
$registration = $burn.SelectSingleNode('b:Registration', $ns)
$container = 'WixAttachedContainer'
if ($burn.GetAttribute('EngineVersion') -cne '7.0.0.0' -or $burn.GetAttribute('Win64') -cne 'yes' -or
    $registration.GetAttribute('Version') -cne $version -or
    $burn.SelectNodes("b:Container[@Id='$container' and @Attached='yes']", $ns).Count -ne 1) {
    throw 'Unexpected fixture engine, version, architecture, or attached container.'
}

$inventory = 'OpenBlade.WindowsNativeRoleInventory.bin'
$hashes = [ordered]@{}
foreach ($name in $inventory,"$service.exe","$agent.exe") {
    $relativePath = "payload\$name"
    $declarations = $burn.SelectNodes("b:Payload[@FilePath='$relativePath']", $ns)
    if ($declarations.Count -ne 1 -or $declarations[0].GetAttribute('Packaging') -cne 'embedded' -or
        $declarations[0].GetAttribute('Container') -cne $container) {
        throw "Unexpected fixture payload declaration for $relativePath."
    }
    $id = $declarations[0].GetAttribute('Id')
    $references = $burn.SelectNodes("b:Chain/*/b:PayloadRef[@Id='$id']", $ns)
    if ($references.Count -ne 1 -or $references[0].ParentNode.GetAttribute('Id') -cne 'OpenBladeSetup') {
        throw "Unexpected fixture package owner for $relativePath."
    }
    $expanded = Join-Path "$extracted/$container" $relativePath
    $hashes[$name] = (Get-FileHash -LiteralPath $expanded -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ([Convert]::ToHexString([IO.File]::ReadAllBytes("$staging/$inventory")) -cne
    [Convert]::ToHexString([IO.File]::ReadAllBytes("$extracted/$container/payload/$inventory"))) {
    throw 'Extracted inventory bytes differ from the authored record.'
}
& dotnet "$encode/InventoryWriter.dll" verify $SourceRevision $version "$extracted/$container/payload"

[ordered]@{
    sourceRevision = $SourceRevision
    productVersion = $version
    target = $target
    wixSdk = '7.0.0'
    bundle = $bundle
    bundleSha256 = $bundleHash
    container = $container
    packageOwner = 'OpenBladeSetup'
    payloadSha256 = $hashes
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $scratch 'proof.json')
