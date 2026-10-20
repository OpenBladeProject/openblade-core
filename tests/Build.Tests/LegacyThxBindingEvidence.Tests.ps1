[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$collectorPath = Join-Path $repository 'build\Export-LegacyThxBindingEvidence.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsContaining {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Expected
    )

    try {
        & $Action
    }
    catch {
        Assert-True `
            -Condition ($_.Exception.Message.IndexOf(
                    $Expected,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) `
            -Message "Expected error containing '$Expected', got '$($_.Exception.Message)'."
        return
    }
    throw "Expected an error containing '$Expected'."
}

function New-HostIdentity {
    param([Parameter(Mandatory)][string]$ProductName)

    return [ordered]@{
        BiosManufacturer = 'Razer'
        BiosProduct = $ProductName
        SystemManufacturer = 'Razer'
        SystemProduct = $ProductName
    }
}

function New-Device {
    param(
        [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string[]]$HardwareIds,
        [string]$ParentInstanceId = '',
        [string]$PublishedInf = '',
        [string]$DriverSection = '',
        [string]$DriverVersion = '',
        [string]$DriverProvider = 'Fixture Provider',
        [string]$DriverKey = '{4d36e96c-e325-11ce-bfc1-08002be10318}\0001',
        [string[]]$ExtendedConfigurationIds = @()
    )

    return [pscustomobject]@{
        InstanceId = $InstanceId
        HardwareIds = $HardwareIds
        ParentInstanceId = $ParentInstanceId
        PublishedInf = $PublishedInf
        DriverSection = $DriverSection
        DriverVersion = $DriverVersion
        DriverProvider = $DriverProvider
        DriverKey = $DriverKey
        ExtendedConfigurationIds = $ExtendedConfigurationIds
    }
}

$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $collectorPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) `
    'The legacy THX evidence collector must parse under Windows PowerShell.'

$source = [IO.File]::ReadAllText($collectorPath)
foreach ($contract in @(
        "ValidateSet('RZ09-0482', 'RZ09-0483', 'RZ09-0484')",
        'DEVPKEY_Device_ExtendedConfigurationIds',
        'WindowsRecordedConfiguration',
        'RecordedNotApplicationProof',
        'SetupGetInfDriverStoreLocation',
        'PrivateDeveloperEvidence',
        'activeEndpointAssociation',
        'MissingEvidence',
        'EvidenceOnly',
        'Partial')) {
    Assert-True ($source.IndexOf($contract, [StringComparison]::Ordinal) -ge 0) `
        "The collector is missing contract '$contract'."
}

foreach ($forbidden in @(
        'RZ09-0581',
        'Invoke-WebRequest',
        'Invoke-RestMethod',
        'DownloadFile',
        'DownloadString',
        'Start-Process',
        'Get-AuthenticodeSignature',
        'signtool',
        'Verb RunAs',
        'Set-ItemProperty',
        'New-ItemProperty',
        'Remove-ItemProperty',
        'Start-Service',
        'Stop-Service',
        '/add-driver',
        '/install',
        '/delete-driver',
        '/uninstall',
        '/restart-device',
        '/disable-device',
        '/enable-device',
        'IMMDevice',
        'MMDevices\Audio')) {
    Assert-True ($source.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "The collector contains forbidden live action '$forbidden'."
}

. $collectorPath

$modelCases = @(
    [pscustomobject]@{
        Model = 'RZ09-0482'
        Product = 'Blade 14 - RZ09-0482'
        Codec = 'HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A582020'
    },
    [pscustomobject]@{
        Model = 'RZ09-0483'
        Product = 'Blade 16 - RZ09-0483'
        Codec = 'INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583006'
    },
    [pscustomobject]@{
        Model = 'RZ09-0484'
        Product = 'Blade 18 - RZ09-0484'
        Codec = 'INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583007'
    }
)

foreach ($case in $modelCases) {
    $codecInstance = $case.Codec + '\PRIVATE-SERIAL'
    $recorded = "oem42.inf:$($case.Codec),DeviceExtension_Install_RT.NT,06/17/2023,2.6.5.0"
    $devices = @(
        (New-Device `
            -InstanceId $codecInstance `
            -HardwareIds @($case.Codec) `
            -PublishedInf 'oem10.inf' `
            -DriverSection 'IntcAzAudModel.NT' `
            -DriverVersion '6.0.9909.1' `
            -ExtendedConfigurationIds @($recorded)),
        (New-Device `
            -InstanceId 'SWC\THXAPO\PRIVATE' `
            -HardwareIds @('SWC\VEN_THX&CID_THXAPO&UNIQUE-SUFFIX') `
            -ParentInstanceId $codecInstance `
            -PublishedInf 'oem11.inf' `
            -DriverSection 'THXAPO_Device.NT' `
            -DriverVersion '2.6.5.0'),
        (New-Device `
            -InstanceId 'SWC\FOREIGN\PRIVATE' `
            -HardwareIds @('SWC\VEN_OTHER&CID_OTHER') `
            -ParentInstanceId $codecInstance `
            -PublishedInf 'oem12.inf'),
        (New-Device `
            -InstanceId 'HDAUDIO\UNRELATED\PRIVATE' `
            -HardwareIds @('HDAUDIO\UNRELATED') `
            -PublishedInf 'oem13.inf')
    )
    $selection = Select-LegacyThxBindingSource `
        -ModelNumber $case.Model `
        -HostIdentity (New-HostIdentity $case.Product) `
        -Devices $devices

    Assert-True ($selection.Nodes.Count -eq 2) `
        "$($case.Model) selection did not stay on the exact codec and THX child chain."
    Assert-True (($selection.Nodes.alias -join '|') -ceq 'node-01|node-02') `
        "$($case.Model) selection did not use deterministic local aliases."
    Assert-True (($selection.Nodes[1].hardwareIds -join '|') -ceq 'SWC\VEN_THX&CID_THXAPO') `
        "$($case.Model) selection exposed a THX child instance suffix."
    Assert-True ($selection.PublishedPackages.Count -eq 3) `
        "$($case.Model) selection missed a bound or Windows-recorded package."
    Assert-True ($selection.RecordedConfigurations.Count -eq 1) `
        "$($case.Model) Windows-recorded configuration was not parsed."
    Assert-True ($selection.RecordedConfigurations[0].Status -eq 'RecordedNotApplicationProof') `
        "$($case.Model) recorded configuration was overstated as applied."
    Assert-True ($selection.RecordedConfigurations[0].Section -eq 'DeviceExtension_Install_RT.NT') `
        "$($case.Model) recorded configuration section changed."
    $publicNodes = $selection.Nodes | ConvertTo-Json -Depth 8
    Assert-True ($publicNodes.IndexOf('PRIVATE', [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "$($case.Model) public nodes exposed a private device-instance suffix."
}

$wrongHost = New-HostIdentity 'Blade 18 - RZ09-0484'
Assert-ThrowsContaining -Expected 'exact host RZ09-0483' -Action {
    Select-LegacyThxBindingSource `
        -ModelNumber 'RZ09-0483' `
        -HostIdentity $wrongHost `
        -Devices @()
}

$pipelineFunctions = @(
    'Get-LiveHostIdentity',
    'Get-LiveDeviceRecords',
    'Resolve-LiveSelectedPackages',
    'Get-LiveRegistryEvidence',
    'Get-LiveSetupApiFacts',
    'Publish-LegacyThxBindingEvidence'
)
$originalPipelineFunctions = @{}
foreach ($functionName in $pipelineFunctions) {
    $originalPipelineFunctions[$functionName] =
        (Get-Item -LiteralPath "Function:\$functionName").ScriptBlock
}
$script:unexpectedPipelineCalls = [Collections.Generic.List[string]]::new()
try {
    Set-Item -LiteralPath Function:\Get-LiveHostIdentity -Value {
        New-HostIdentity 'Blade 18 - RZ09-0484'
    }
    Set-Item -LiteralPath Function:\Get-LiveDeviceRecords -Value {
        param($Profile)
        $script:unexpectedPipelineCalls.Add('devices')
        throw 'Device enumeration must not run for the wrong host.'
    }
    Set-Item -LiteralPath Function:\Resolve-LiveSelectedPackages -Value {
        param($Selection)
        $script:unexpectedPipelineCalls.Add('packages')
        throw 'Package resolution must not run for the wrong host.'
    }
    Set-Item -LiteralPath Function:\Get-LiveRegistryEvidence -Value {
        param($DriverKey)
        $script:unexpectedPipelineCalls.Add('registry')
        throw 'Registry evidence must not run for the wrong host.'
    }
    Set-Item -LiteralPath Function:\Get-LiveSetupApiFacts -Value {
        param($Packages)
        $script:unexpectedPipelineCalls.Add('setupapi')
        throw 'SetupAPI evidence must not run for the wrong host.'
    }
    Set-Item -LiteralPath Function:\Publish-LegacyThxBindingEvidence -Value {
        param($Destination, $Manifest, $Packages, $AllowedSourceRoot)
        $script:unexpectedPipelineCalls.Add('output')
        throw 'Output publication must not run for the wrong host.'
    }
    Assert-ThrowsContaining -Expected 'exact host RZ09-0482' -Action {
        Invoke-LegacyThxBindingEvidenceExport `
            -RequestedModel 'RZ09-0482' `
            -RequestedOutputDirectory 'unused-fixture-output'
    }
    Assert-True ($script:unexpectedPipelineCalls.Count -eq 0) `
        'The wrong-host refusal read devices, packages, registry, SetupAPI, or output state.'
}
finally {
    foreach ($functionName in $pipelineFunctions) {
        Set-Item -LiteralPath "Function:\$functionName" `
            -Value $originalPipelineFunctions[$functionName]
    }
}

Assert-True ($null -eq (Parse-WindowsRecordedConfiguration `
            -Value 'oem9.inf:HDAUDIO\OTHER,Section.NT,01/01/2026,1.2.3.4' `
            -ExpectedHardwareId $modelCases[0].Codec)) `
    'A Windows-recorded configuration for another hardware ID was accepted.'
Assert-True ($null -eq (Parse-WindowsRecordedConfiguration `
            -Value 'not-a-recorded-configuration' `
            -ExpectedHardwareId $modelCases[0].Codec)) `
    'Malformed Windows-recorded configuration was accepted.'
$recordedIdentity = [pscustomobject]@{
    HardwareId = $modelCases[0].Codec
    Section = 'DeviceExtension_Install_RT.NT'
    DriverDate = '06/17/2023'
    DriverVersion = '2.6.5.0'
}
$matchingInf = @"
[Version]
DriverVer=06/17/2023,2.6.5.0
[Models]
%Device%=DeviceExtension_Install_RT.NT,$($modelCases[0].Codec)
[DeviceExtension_Install_RT.NT]
"@
Assert-True (Test-WindowsRecordedConfigurationIdentity `
        -Source $recordedIdentity `
        -InfText $matchingInf) `
    'A matching Windows-recorded source identity was rejected.'
Assert-True (-not (Test-WindowsRecordedConfigurationIdentity `
            -Source $recordedIdentity `
            -InfText ($matchingInf.Replace('2.6.5.0', '9.9.9.9')))) `
    'A recycled OEM alias with a conflicting source version was accepted.'
$wrongSectionBindingInf = $matchingInf.Replace(
    "%Device%=DeviceExtension_Install_RT.NT,$($modelCases[0].Codec)",
    "%Device%=DifferentSection.NT,$($modelCases[0].Codec)`r`n%Other%=DeviceExtension_Install_RT.NT,HDAUDIO\OTHER")
Assert-True (-not (Test-WindowsRecordedConfigurationIdentity `
            -Source $recordedIdentity `
            -InfText $wrongSectionBindingInf)) `
    'An INF with independent hardware and section matches was accepted as the recorded binding.'
$boundIdentity = [pscustomobject]@{
    HardwareId = 'SWC\VEN_THX&CID_THXAPO'
    VerificationHardwareIds = @(
        'SWC\VEN_THX&CID_THXAPO&FIRST',
        'SWC\VEN_THX&CID_THXAPO&BOUND')
    Section = 'THXAPO_Device.NT'
    DriverVersion = '2.6.5.0'
}
$boundInf = @"
[Version]
DriverVer=06/17/2023,2.6.5.0
[Models]
%Device%=THXAPO_Device.NT,SWC\VEN_THX&CID_THXAPO&BOUND
[THXAPO_Device.NT]
"@
Assert-True (Test-InfPackageSourceIdentity -Source $boundIdentity -InfText $boundInf) `
    'A bound package using a later raw PnP hardware ID was rejected.'

$recordedPackage = [pscustomobject]@{
    PublishedInf = 'oem42.inf'
    PackageAlias = 'package-01'
    Manifest = [ordered]@{
        sources = @([ordered]@{
                source = 'WindowsRecordedConfiguration'
                hardwareId = $recordedIdentity.HardwareId
                section = $recordedIdentity.Section
                driverDate = $recordedIdentity.DriverDate
                driverVersion = $recordedIdentity.DriverVersion
                sourceIdentity = 'SourceIdentityMismatch'
            })
    }
}
$recordedPublicSource = [pscustomobject]@{
    PublishedInf = 'oem42.inf'
    HardwareId = $recordedIdentity.HardwareId
    Section = $recordedIdentity.Section
    DriverDate = $recordedIdentity.DriverDate
    DriverVersion = $recordedIdentity.DriverVersion
    Source = 'WindowsRecordedConfiguration'
    Status = 'RecordedNotApplicationProof'
}
$publicRecorded = ConvertTo-PublicRecordedConfigurations `
    -RecordedConfigurations @($recordedPublicSource) `
    -Packages @($recordedPackage)
Assert-True ($publicRecorded[0].packageAlias -eq $null -and
    $publicRecorded[0].sourceIdentity -eq 'SourceIdentityMismatch') `
    'A mismatched recycled published-INF alias was linked to a copied package.'
$recordedPackage.Manifest.sources[0].sourceIdentity = 'VerifiedAgainstResolvedInf'
$publicRecorded = ConvertTo-PublicRecordedConfigurations `
    -RecordedConfigurations @($recordedPublicSource) `
    -Packages @($recordedPackage)
Assert-True ($publicRecorded[0].packageAlias -eq 'package-01') `
    'A verified recorded source was not linked to its copied package alias.'

$registry = ConvertTo-LegacyThxRegistryEvidence -Trees @(
    [pscustomobject]@{
        Path = 'InterfaceSetting\Render'
        Values = @(
            [pscustomobject]@{ Name = 'RearLineOutTopoHAP'; Kind = 'MultiString'; Value = @('SysFx', 'OEMSettingsOverride') },
            [pscustomobject]@{ Name = '{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D},13'; Kind = 'String'; Value = '{11111111-2222-3333-4444-555555555555}' })
    },
    [pscustomobject]@{
        Path = 'ReferencedSource\SysFx\FX\0'
        Values = @(
            [pscustomobject]@{ Name = 'CompositeFX'; Kind = 'String'; Value = '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}' },
            [pscustomobject]@{ Name = 'Blob'; Kind = 'Binary'; Value = [byte[]](1, 2, 3, 4) })
    },
    [pscustomobject]@{
        Path = 'ReferencedSource\OEMSettingsOverride\EP\0'
        Values = @(
            [pscustomobject]@{ Name = 'DeviceType'; Kind = 'DWord'; Value = 2 })
    })
Assert-True ($registry.Count -eq 3) 'The typed registry evidence lost a selected source tree.'
Assert-True ($null -eq $registry[1].values[1].data -and
    $registry[1].values[1].status -eq 'RedactedNonStructural') `
    'Opaque binary FX evidence was emitted instead of being marked redacted.'
Assert-True (($registry[0].values[0].data -join '|') -ceq 'SysFx|OEMSettingsOverride') `
    'The InterfaceSetting section list was not preserved in order.'
Assert-ThrowsContaining -Expected 'outside InterfaceSetting' -Action {
    ConvertTo-LegacyThxRegistryEvidence -Trees @(
        [pscustomobject]@{ Path = 'Unbounded\Registry'; Values = @() })
}
Assert-ThrowsContaining -Expected 'outside the evidence whitelist' -Action {
    ConvertTo-TypedRegistryValue -Name 'Path\Leak' -Kind 'String' -Value 'value'
}
$redactedPath = ConvertTo-TypedRegistryValue `
    -Name 'FriendlyName' `
    -Kind 'String' `
    -Value 'C:\Users\Alice\private-value'
Assert-True ($null -eq $redactedPath.data -and
    $redactedPath.status -eq 'RedactedNonStructural') `
    'An arbitrary absolute registry string was emitted as binding evidence.'
$capturedClsid = ConvertTo-TypedRegistryValue `
    -Name 'CompositeFX' `
    -Kind 'String' `
    -Value '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}'
Assert-True ($capturedClsid.data -eq '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}' -and
    $capturedClsid.status -eq 'Captured') `
    'A structural FX CLSID was not preserved.'
Assert-ThrowsContaining -Expected 'value count exceeds' -Action {
    $tooManyValues = @(
        for ($index = 0; $index -le $script:MaximumRegistryValues; $index++) {
            [pscustomobject]@{ Name = "V$index"; Kind = 'DWord'; Value = $index }
        })
    ConvertTo-LegacyThxRegistryEvidence -Trees @(
        [pscustomobject]@{ Path = 'InterfaceSetting'; Values = $tooManyValues })
}

$factPackage = [pscustomobject]@{
    PublishedInf = 'oem42.inf'
    PackageAlias = 'package-01'
    OriginalInf = 'THXHDAudio.inf'
    Section = 'DeviceExtension_Install_RT.NT'
}
$facts = Get-SanitizedSetupApiFacts -Lines @(
    '>>>  [Device Install]',
    '>>>  Section start 2026/10/20 12:34:56.000',
    'inf: Opened INF oem42.inf',
    'inf: DeviceExtension_Install_RT.NT',
    '<<<  Section end') -Packages @($factPackage)
Assert-True ($facts.Count -eq 1 -and $facts[0].status -eq 'HistoricalAttributionUnverified') `
    'A historical SetupAPI alias was overstated as attributable provenance.'
Assert-True ($facts[0].time -eq '2026/10/20 12:34:56.000') `
    'The bounded SetupAPI timestamp was not retained.'
Assert-True ($facts[0].logOrder -eq 2) `
    'The SetupAPI fact did not retain its bounded log order.'
Assert-True (($facts | ConvertTo-Json -Depth 6) -notmatch '(?i)oem[0-9]+[.]inf') `
    'Structured SetupAPI facts exposed a machine-local published INF alias.'
$ambiguousFacts = Get-SanitizedSetupApiFacts -Lines @(
    'oem42.inf',
    'DeviceExtension_Install_RT.NT',
    ('padding' * 40),
    'oem42.inf',
    'DeviceExtension_Install_RT.NT') -Packages @($factPackage)
Assert-True ($ambiguousFacts[0].status -eq 'AmbiguousHistoricalAlias') `
    'Repeated historical OEM aliases were incorrectly treated as unique provenance.'
$missingFacts = Get-SanitizedSetupApiFacts -Lines @('no relevant package') -Packages @($factPackage)
Assert-True ($missingFacts[0].status -eq 'MissingEvidence') `
    'Missing SetupAPI provenance was not reported explicitly.'

foreach ($privateValue in @(
        'oem42.inf',
        'C:\Users\Alice\capture',
        '\\?\C:\private',
        'SWD\MMDEVAPI\{endpoint}',
        'S-1-5-21-1234')) {
    Assert-ThrowsContaining -Expected 'forbidden machine-local identity' -Action {
        Assert-PublicEvidenceIsSanitized -Manifest ([ordered]@{ value = $privateValue })
    }
}
Assert-ThrowsContaining -Expected 'full codec device-instance identity' -Action {
    Assert-PublicEvidenceIsSanitized -Manifest ([ordered]@{
            value = $modelCases[0].Codec + '\PRIVATE-SERIAL'
        })
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'openblade-legacy-thx-evidence-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$junction = $null
try {
    $sourceRoot = [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'source')).FullName
    $infPath = Join-Path $sourceRoot 'THXHDAudio.inf'
    $catPath = Join-Path $sourceRoot 'THXHDAudio.cat'
    $infBytes = [Text.Encoding]::ASCII.GetBytes("[Version]`r`nCatalogFile=THXHDAudio.cat`r`n")
    [IO.File]::WriteAllBytes($infPath, $infBytes)
    [IO.File]::WriteAllBytes($catPath, [byte[]](9, 8, 7, 6))
    $packageManifest = [ordered]@{
        alias = 'package-01'
        originalInf = 'THXHDAudio.inf'
        sources = @([ordered]@{
                source = 'WindowsRecordedConfiguration'
                node = $null
                hardwareId = $modelCases[0].Codec
                section = 'DeviceExtension_Install_RT.NT'
                driverDate = '06/17/2023'
                driverVersion = '2.6.5.0'
                status = 'RecordedNotApplicationProof'
            })
        inf = [ordered]@{ size = 0; sha256 = '' }
        catalog = [ordered]@{
            file = 'THXHDAudio.cat'
            size = 0
            sha256 = ''
            signatureStatus = 'PendingOfflineReview'
            signerSubject = $null
            signerThumbprint = $null
            infMembership = 'PendingOfflineReview'
        }
    }
    $manifest = [ordered]@{
        manifestType = 'OpenBlade.LegacyThxBindingEvidence'
        schemaVersion = 1
        classification = 'PrivateDeveloperEvidence'
        conclusion = 'Partial'
        modelNumber = 'RZ09-0482'
        codecHardwareId = $modelCases[0].Codec
        activeEndpointAssociation = 'MissingEvidence'
        nodes = @([ordered]@{
                alias = 'node-01'
                role = 'ExactCodec'
                parent = $null
                hardwareIds = @($modelCases[0].Codec)
            })
        packages = @($packageManifest)
    }
    $package = [pscustomobject]@{
        PackageAlias = 'package-01'
        OriginalInf = 'THXHDAudio.inf'
        CatalogFile = 'THXHDAudio.cat'
        InfPath = $infPath
        CatalogPath = $catPath
        Manifest = $packageManifest
    }
    $destination = Join-Path $fixtureRoot 'published'
    $published = Publish-LegacyThxBindingEvidence `
        -Destination $destination `
        -Manifest $manifest `
        -Packages @($package) `
        -AllowedSourceRoot $sourceRoot
    Assert-True ([string]::Equals($published, $destination, [StringComparison]::OrdinalIgnoreCase)) `
        'The evidence publisher returned the wrong directory.'
    $copiedInf = Join-Path $destination 'packages\package-01\THXHDAudio.inf'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($copiedInf)) -eq
        [Convert]::ToBase64String($infBytes)) `
        'The original CRLF INF bytes changed during evidence publication.'
    Assert-True ((Get-FileSha256 $copiedInf) -eq (Get-FileSha256 $infPath)) `
        'The published INF hash differs from its original source.'
    $publishedManifest = [IO.File]::ReadAllText((Join-Path $destination 'manifest.json'))
    Assert-True ($publishedManifest -notmatch '(?i)oem[0-9]+[.]inf|PRIVATE-SERIAL|C:\\Users\\') `
        'Published evidence contains a forbidden private identity.'
    Assert-True ($publishedManifest.IndexOf('PendingOfflineReview', [StringComparison]::Ordinal) -ge 0) `
        'Offline signature and catalog-membership review was not retained as pending evidence.'
    Assert-ThrowsContaining -Expected 'already exists' -Action {
        Publish-LegacyThxBindingEvidence `
            -Destination $destination `
            -Manifest $manifest `
            -Packages @($package) `
            -AllowedSourceRoot $sourceRoot
    }

    $outside = Join-Path $fixtureRoot 'outside.inf'
    [IO.File]::WriteAllBytes($outside, $infBytes)
    Assert-ThrowsContaining -Expected 'escapes the admitted root' -Action {
        Copy-ExactEvidenceFile `
            -Source $outside `
            -Destination (Join-Path $sourceRoot 'escape.inf') `
            -AllowedSourceRoot $sourceRoot
    }

    $junctionTarget = [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'junction-target')).FullName
    [IO.File]::WriteAllBytes((Join-Path $junctionTarget 'linked.inf'), $infBytes)
    $junction = Join-Path $sourceRoot 'junction'
    New-Item -ItemType Junction -Path $junction -Target $junctionTarget -ErrorAction Stop | Out-Null
    Assert-ThrowsContaining -Expected 'reparse point' -Action {
        Copy-ExactEvidenceFile `
            -Source (Join-Path $junction 'linked.inf') `
            -Destination (Join-Path $sourceRoot 'linked-copy.inf') `
            -AllowedSourceRoot $sourceRoot
    }
    Assert-ThrowsContaining -Expected 'reparse point' -Action {
        Assert-NoReparsePointsInExistingPath -Path $junction
    }
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedFixture.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedFixture).StartsWith(
            'openblade-legacy-thx-evidence-test-',
            [StringComparison]::Ordinal)) {
        if ($null -ne $junction -and
            (Test-Path -LiteralPath $junction) -and
            ([IO.Path]::GetFullPath($junction)).StartsWith(
                $resolvedFixture + '\',
                [StringComparison]::OrdinalIgnoreCase) -and
            (((Get-Item -Force -LiteralPath $junction).Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            [IO.Directory]::Delete($junction, $false)
        }
        [IO.Directory]::Delete($resolvedFixture, $true)
    }
}

$tooManyDevices = @(
    for ($index = 0; $index -le $script:MaximumDevices; $index++) {
        [pscustomobject]@{ InstanceId = "fixture-$index"; HardwareIds = @() }
    })
Assert-ThrowsContaining -Expected 'inventory exceeds' -Action {
    Select-LegacyThxBindingSource `
        -ModelNumber 'RZ09-0482' `
        -HostIdentity (New-HostIdentity 'Blade 14 - RZ09-0482') `
        -Devices $tooManyDevices
}

Write-Output 'Legacy THX binding evidence contracts passed.'
