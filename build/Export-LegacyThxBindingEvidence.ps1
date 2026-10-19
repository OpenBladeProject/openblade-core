[CmdletBinding()]
param(
    [ValidateSet('RZ09-0482', 'RZ09-0483', 'RZ09-0484')]
    [string]$Model,
    [string]$OutputDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:Profiles = @{
    'RZ09-0482' = [ordered]@{
        ModelNumber = 'RZ09-0482'
        ProductName = 'Blade 14 - RZ09-0482'
        CodecHardwareId = 'HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A582020'
    }
    'RZ09-0483' = [ordered]@{
        ModelNumber = 'RZ09-0483'
        ProductName = 'Blade 16 - RZ09-0483'
        CodecHardwareId = 'INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583006'
    }
    'RZ09-0484' = [ordered]@{
        ModelNumber = 'RZ09-0484'
        ProductName = 'Blade 18 - RZ09-0484'
        CodecHardwareId = 'INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583007'
    }
}
$script:ThxChildHardwareIds = @(
    'SWC\VEN_THX&CID_THXAPO',
    'SWC\VEN_THX&PID_THXSVC',
    'SWC\VEN_THX&PID_THXHSA',
    'SWC\VEN_THX&PID_THXSCU'
)
$script:MaximumDevices = 8192
$script:MaximumPackages = 16
$script:MaximumRegistryValues = 512
$script:MaximumRegistryDepth = 6
$script:MaximumSetupLogBytes = 67108864
$script:MaximumSetupFacts = 256
$script:DriverStoreRoot = Join-Path $env:WINDIR 'System32\DriverStore\FileRepository'

function Get-LegacyThxProfile {
    param([Parameter(Mandatory)][string]$ModelNumber)

    if (-not $script:Profiles.ContainsKey($ModelNumber)) {
        throw "The collector supports only RZ09-0482, RZ09-0483, and RZ09-0484."
    }
    return $script:Profiles[$ModelNumber]
}

function Get-StringArray {
    param($Value)

    if ($null -eq $Value) {
        return @()
    }
    return @($Value | ForEach-Object { [string]$_ })
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Test-ExactHostIdentity {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Profile,
        [Parameter(Mandatory)][Collections.IDictionary]$Identity
    )

    return [string]::Equals([string]$Identity.BiosManufacturer, 'Razer', [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Identity.SystemManufacturer, 'Razer', [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Identity.BiosProduct, [string]$Profile.ProductName, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Identity.SystemProduct, [string]$Profile.ProductName, [StringComparison]::Ordinal)
}

function Parse-WindowsRecordedConfiguration {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$ExpectedHardwareId
    )

    if ($Value.Length -gt 4096) {
        throw 'A Windows recorded configuration entry exceeds the collector bound.'
    }
    $match = [regex]::Match(
        $Value,
        '^(oem[0-9]+[.]inf):([^,]+),([^,]+),([0-9]{2}/[0-9]{2}/[0-9]{4}),([0-9]+(?:[.][0-9]+){1,3})$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $null
    }
    if (-not [string]::Equals(
            $match.Groups[2].Value,
            $ExpectedHardwareId,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    return [pscustomobject]@{
        PublishedInf = $match.Groups[1].Value.ToLowerInvariant()
        HardwareId = $ExpectedHardwareId
        Section = $match.Groups[3].Value
        DriverDate = $match.Groups[4].Value
        DriverVersion = $match.Groups[5].Value
        Source = 'WindowsRecordedConfiguration'
        Status = 'RecordedNotApplicationProof'
    }
}

function Test-ThxChildHardwareId {
    param([Parameter(Mandatory)][string]$HardwareId)

    foreach ($expected in $script:ThxChildHardwareIds) {
        if ([string]::Equals($HardwareId, $expected, [StringComparison]::OrdinalIgnoreCase) -or
            $HardwareId.StartsWith($expected + '&', [StringComparison]::OrdinalIgnoreCase) -or
            $HardwareId.StartsWith($expected + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Get-CanonicalThxChildHardwareId {
    param([Parameter(Mandatory)][string]$HardwareId)

    foreach ($expected in $script:ThxChildHardwareIds) {
        if ([string]::Equals($HardwareId, $expected, [StringComparison]::OrdinalIgnoreCase) -or
            $HardwareId.StartsWith($expected + '&', [StringComparison]::OrdinalIgnoreCase) -or
            $HardwareId.StartsWith($expected + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return $expected
        }
    }
    return $null
}

function Select-LegacyThxBindingSource {
    param(
        [Parameter(Mandatory)][string]$ModelNumber,
        [Parameter(Mandatory)][Collections.IDictionary]$HostIdentity,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Devices
    )

    $profile = Get-LegacyThxProfile -ModelNumber $ModelNumber
    if (-not (Test-ExactHostIdentity -Profile $profile -Identity $HostIdentity)) {
        throw "The two Windows identity sources do not confirm exact host $ModelNumber."
    }
    if ($Devices.Count -gt $script:MaximumDevices) {
        throw 'The present-device inventory exceeds the collector bound.'
    }

    $codecs = @($Devices | Where-Object {
            (Get-StringArray $_.HardwareIds) -contains $profile.CodecHardwareId
        })
    if ($codecs.Count -ne 1) {
        throw "Expected one exact $ModelNumber codec, found $($codecs.Count)."
    }
    $codec = $codecs[0]
    if ([string]::IsNullOrWhiteSpace([string]$codec.InstanceId)) {
        throw 'The exact codec has no internal device identity.'
    }

    $selected = [Collections.Generic.List[object]]::new()
    $selected.Add($codec)
    foreach ($device in $Devices) {
        if (-not [string]::Equals(
                [string]$device.ParentInstanceId,
                [string]$codec.InstanceId,
                [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (@(Get-StringArray $device.HardwareIds | Where-Object { Test-ThxChildHardwareId $_ }).Count -gt 0) {
            $selected.Add($device)
        }
    }

    $nodeRecords = @()
    $packageSources = [Collections.Generic.List[object]]::new()
    $nodeIndex = 0
    foreach ($device in $selected) {
        $nodeIndex++
        $alias = 'node-{0:D2}' -f $nodeIndex
        $isCodec = [string]::Equals(
            [string]$device.InstanceId,
            [string]$codec.InstanceId,
            [StringComparison]::OrdinalIgnoreCase)
        $hardware = if ($isCodec) {
            @($profile.CodecHardwareId)
        }
        else {
            @(Get-StringArray $device.HardwareIds |
                ForEach-Object { Get-CanonicalThxChildHardwareId $_ } |
                Where-Object { $null -ne $_ } |
                Sort-Object -Unique)
        }
        $verificationHardwareIds = if ($isCodec) {
            @($profile.CodecHardwareId)
        }
        else {
            @(Get-StringArray $device.HardwareIds | Where-Object { Test-ThxChildHardwareId $_ })
        }
        $nodeRecords += [ordered]@{
            alias = $alias
            role = if ($isCodec) { 'ExactCodec' } else { 'ThxChild' }
            parent = if ($isCodec) { $null } else { 'node-01' }
            hardwareIds = $hardware
            provider = [string]$device.DriverProvider
            version = [string]$device.DriverVersion
            section = [string]$device.DriverSection
        }
        $published = [string]$device.PublishedInf
        if (-not [string]::IsNullOrWhiteSpace($published)) {
            if ($published -notmatch '^(?i)oem[0-9]+[.]inf$') {
                throw 'A selected device has an invalid Windows published INF alias.'
            }
            $packageSources.Add([pscustomobject]@{
                    PublishedInf = $published.ToLowerInvariant()
                    Source = 'BoundDevice'
                    NodeAlias = $alias
                    Section = [string]$device.DriverSection
                    HardwareId = $hardware[0]
                    VerificationHardwareIds = $verificationHardwareIds
                    DriverDate = $null
                    DriverVersion = [string]$device.DriverVersion
                    Status = 'BoundAtCollection'
                })
        }
    }

    $recorded = @()
    foreach ($value in Get-StringArray $codec.ExtendedConfigurationIds) {
        $parsed = Parse-WindowsRecordedConfiguration `
            -Value $value `
            -ExpectedHardwareId $profile.CodecHardwareId
        if ($null -ne $parsed) {
            $recorded += $parsed
            $packageSources.Add($parsed)
        }
    }
    $distinctPackages = @($packageSources | Group-Object PublishedInf | ForEach-Object { $_.Group[0] })
    if ($distinctPackages.Count -eq 0 -or $distinctPackages.Count -gt $script:MaximumPackages) {
        throw 'The exact selected chain did not produce a bounded package set.'
    }

    return [pscustomobject]@{
        Profile = $profile
        CodecInstanceId = [string]$codec.InstanceId
        CodecDriverKey = [string]$codec.DriverKey
        Nodes = $nodeRecords
        PackageSources = @($packageSources)
        PublishedPackages = $distinctPackages
        RecordedConfigurations = $recorded
    }
}

function Test-SafeRelativeRegistryPath {
    param([Parameter(Mandatory)][string]$Path)

    return $Path.Length -le 512 -and
        $Path -notmatch '(^|[\\/])[.]{1,2}([\\/]|$)' -and
        $Path -match '^[A-Za-z0-9_.{}(), -]+(?:\\[A-Za-z0-9_.{}(), -]+)*$'
}

function Test-StructuralRegistryString {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value.Length -eq 0 -or $Value.Length -gt 1024) {
        return $false
    }
    if ($Value -match '(?i)[A-Z]:\\|\\\\|S-1-[0-9-]+|SWD\\MMDEVAPI|oem[0-9]+[.]inf') {
        return $false
    }
    $setting = '(?:SysFx|SysRecFx|OEMSettingsOverride|OutAPO|HPAPO|CompositeFX[A-Za-z0-9_.-]*|[SMELG]FX[A-Za-z0-9_.-]*)'
    if ($Value -match '^\{[0-9A-Fa-f-]{36}\}$' -or
        $Value -match '^[0-9A-Fa-f-]{36}$' -or
        $Value -match ('^(?i)' + $setting + '(?:\\(?:FX|EP)\\0)?$') -or
        $Value -match '^%ProgramData%\\THX\\[A-Za-z0-9_. -]+$') {
        return $true
    }
    foreach ($profile in $script:Profiles.Values) {
        if ([string]::Equals($Value, [string]$profile.CodecHardwareId, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return Test-ThxChildHardwareId -HardwareId $Value
}

function Test-InterfaceSettingReference {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value.Length -eq 0 -or $Value.Length -gt 128) {
        return $false
    }
    $setting = '(?:SysFx|SysRecFx|OEMSettingsOverride|OutAPO[A-Za-z0-9_.-]*|HPAPO[A-Za-z0-9_.-]*|CompositeFX[A-Za-z0-9_.-]*|[SMELG]FX[A-Za-z0-9_.-]*)'
    return $Value -match ('^(?i)' + $setting + '$')
}

function ConvertTo-TypedRegistryValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)]$Value
    )

    if ($Name.Length -gt 256 -or $Name -notmatch '^[A-Za-z0-9_.{}(), -]*$') {
        throw 'A selected registry value name is outside the evidence whitelist.'
    }
    $allowedKinds = @('String', 'ExpandString', 'MultiString', 'DWord', 'QWord', 'Binary')
    if ($Kind -notin $allowedKinds) {
        throw "Registry kind '$Kind' is outside the evidence whitelist."
    }
    $status = 'Captured'
    $redactedCount = 0
    if ($Kind -eq 'Binary') {
        $bytes = [byte[]]$Value
        if ($bytes.Length -gt 65536) {
            throw 'A selected binary registry value exceeds the evidence bound.'
        }
        $data = $null
        $status = 'RedactedNonStructural'
        $redactedCount = $bytes.Length
    }
    elseif ($Kind -eq 'MultiString') {
        $items = @(Get-StringArray $Value)
        if ($items.Count -gt 64) {
            throw 'A selected multi-string registry value exceeds the evidence bound.'
        }
        $data = @($items | Where-Object { Test-StructuralRegistryString -Value $_ })
        $redactedCount = $items.Count - $data.Count
        if ($redactedCount -gt 0) {
            $status = 'PartialRedaction'
        }
    }
    elseif ($Kind -eq 'String' -or $Kind -eq 'ExpandString') {
        $text = [string]$Value
        if (Test-StructuralRegistryString -Value $text) {
            $data = $text
        }
        else {
            $data = $null
            $status = 'RedactedNonStructural'
            $redactedCount = 1
        }
    }
    else {
        $data = $Value
    }
    return [ordered]@{
        name = if ($Name.Length -eq 0) { '(Default)' } else { $Name }
        kind = $Kind
        data = $data
        status = $status
        redactedCount = $redactedCount
    }
}

function ConvertTo-LegacyThxRegistryEvidence {
    param([Parameter(Mandatory)][object[]]$Trees)

    if ($Trees.Count -gt 64) {
        throw 'The registry-source tree count exceeds the collector bound.'
    }
    $count = 0
    $output = @()
    foreach ($tree in $Trees) {
        $path = [string]$tree.Path
        if (-not (Test-SafeRelativeRegistryPath -Path $path) -or
            ($path -notmatch '^(?i)InterfaceSetting(?:\\|$)' -and
             $path -notmatch '^(?i)ReferencedSource\\[A-Za-z0-9_.{}(), -]+\\(?:FX|EP)\\0(?:\\|$)')) {
            throw "Registry source '$path' is outside InterfaceSetting and referenced FX/EP evidence."
        }
        $values = @()
        foreach ($value in @($tree.Values)) {
            $count++
            if ($count -gt $script:MaximumRegistryValues) {
                throw 'The registry-source value count exceeds the collector bound.'
            }
            $values += ConvertTo-TypedRegistryValue `
                -Name ([string]$value.Name) `
                -Kind ([string]$value.Kind) `
                -Value $value.Value
        }
        $output += [ordered]@{
            path = $path.Replace('\', '/')
            values = $values
        }
    }
    return $output
}

function Get-SanitizedSetupApiFacts {
    param(
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][object[]]$Packages
    )

    $facts = [Collections.Generic.List[object]]::new()
    foreach ($package in $Packages) {
        $published = [string]$package.PublishedInf
        if ($published -notmatch '^(?i)oem[0-9]+[.]inf$') {
            throw 'SetupAPI provenance received an invalid published INF alias.'
        }
        $indices = @()
        for ($index = 0; $index -lt $Lines.Count; $index++) {
            if ($Lines[$index].IndexOf($published, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $indices += $index
            }
        }
        if ($indices.Count -eq 0) {
            $facts.Add([ordered]@{
                    logOrder = $null
                    packageAlias = [string]$package.PackageAlias
                    originalInf = [string]$package.OriginalInf
                    section = [string]$package.Section
                    time = $null
                    status = 'MissingEvidence'
                    source = 'SetupApiDevLog'
                })
            continue
        }

        $section = [string]$package.Section
        $candidateWindows = @()
        foreach ($index in $indices) {
            $start = [Math]::Max(0, $index - 32)
            $end = [Math]::Min($Lines.Count - 1, $index + 32)
            $window = $Lines[$start..$end]
            $sectionSeen = [string]::IsNullOrWhiteSpace($section) -or
                (@($window | Where-Object {
                            $_.IndexOf($section, [StringComparison]::OrdinalIgnoreCase) -ge 0
                        }).Count -gt 0)
            if ($sectionSeen) {
                $candidateWindows += [pscustomobject]@{ Index = $index; Lines = $window }
            }
        }
        if ($candidateWindows.Count -ne 1) {
            $status = if ($candidateWindows.Count -eq 0) { 'MissingEvidence' } else { 'AmbiguousHistoricalAlias' }
            $time = $null
            $logOrder = $null
        }
        else {
            $status = 'HistoricalAttributionUnverified'
            $time = $null
            $logOrder = [int]$candidateWindows[0].Index
            foreach ($line in $candidateWindows[0].Lines) {
                $timeMatch = [regex]::Match(
                    $line,
                    'Section start ([0-9]{4}/[0-9]{2}/[0-9]{2} [0-9:.]+)',
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($timeMatch.Success) {
                    $time = $timeMatch.Groups[1].Value
                    break
                }
            }
        }
        $facts.Add([ordered]@{
                logOrder = $logOrder
                packageAlias = [string]$package.PackageAlias
                originalInf = [string]$package.OriginalInf
                section = $section
                time = $time
                status = $status
                source = 'SetupApiDevLog'
            })
        if ($facts.Count -gt $script:MaximumSetupFacts) {
            throw 'The SetupAPI provenance fact count exceeds the collector bound.'
        }
    }
    return ,@($facts)
}

function Assert-NoReparsePointsUnderRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence source escapes the admitted root: $Path"
    }
    $current = Get-Item -Force -LiteralPath $fullPath -ErrorAction Stop
    while ($null -ne $current -and
        $current.FullName.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Evidence source contains a reparse point: $($current.FullName)"
        }
        if ([string]::Equals($current.FullName, $fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = if ($current -is [IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Copy-ExactEvidenceFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$AllowedSourceRoot
    )

    Assert-NoReparsePointsUnderRoot -Path $Source -Root $AllowedSourceRoot
    if (Test-Path -LiteralPath $Destination) {
        throw "Evidence destination already exists: $Destination"
    }
    $sourceInfo = Get-Item -LiteralPath $Source -Force -ErrorAction Stop
    if ($sourceInfo.PSIsContainer -or $sourceInfo.Length -gt 16777216) {
        throw 'The selected INF/CAT source is not a bounded file.'
    }
    $beforeHash = Get-FileSha256 -Path $Source
    $input = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $input.CopyTo($output)
            $output.Flush()
        }
        finally {
            $output.Dispose()
        }
    }
    finally {
        $input.Dispose()
    }
    $destinationInfo = Get-Item -LiteralPath $Destination -Force -ErrorAction Stop
    $afterHash = Get-FileSha256 -Path $Destination
    if ($sourceInfo.Length -ne $destinationInfo.Length -or
        -not [string]::Equals($beforeHash, $afterHash, [StringComparison]::Ordinal)) {
        throw 'An original INF/CAT changed while it was copied.'
    }
    return [ordered]@{
        size = [long]$sourceInfo.Length
        sha256 = $beforeHash
    }
}

function Assert-PublicEvidenceIsSanitized {
    param([Parameter(Mandatory)]$Manifest)

    $json = $Manifest | ConvertTo-Json -Depth 20
    $plainJson = [regex]::Unescape($json)
    foreach ($forbidden in @(
            '(?i)oem[0-9]+[.]inf',
            '(?i)[A-Z]:\\Users\\',
            '(?i)\\\\[?]\\',
            '(?i)SWD\\MMDEVAPI',
            '(?i)S-1-[0-9-]+')) {
        if ($plainJson -match $forbidden) {
            throw 'The manifest contains a forbidden machine-local identity.'
        }
    }
    foreach ($profile in $script:Profiles.Values) {
        $escaped = [regex]::Escape([string]$profile.CodecHardwareId)
        if ($plainJson -match ($escaped + '\\')) {
            throw 'The manifest contains a full codec device-instance identity.'
        }
    }
}

function Test-OwnedStagingPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$FinalLeaf
    )

    return [string]::Equals(
            [IO.Path]::GetFullPath((Split-Path -Parent $Path)),
            [IO.Path]::GetFullPath($Parent),
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $Path).StartsWith(".$FinalLeaf.staging-", [StringComparison]::Ordinal)
}

function Assert-NoReparsePointsInExistingPath {
    param([Parameter(Mandatory)][string]$Path)

    $current = Get-Item -Force -LiteralPath ([IO.Path]::GetFullPath($Path)) -ErrorAction Stop
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The output path contains a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Publish-LegacyThxBindingEvidence {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][object[]]$Packages,
        [Parameter(Mandatory)][string]$AllowedSourceRoot
    )

    $resolved = [IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $resolved) {
        throw "The output directory already exists: $resolved"
    }
    $parent = Split-Path -Parent $resolved
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw 'The output parent directory must already exist.'
    }
    Assert-NoReparsePointsInExistingPath -Path $parent
    $repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    if ($resolved.StartsWith($repository.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Private binding evidence must be written outside the repository.'
    }

    Assert-PublicEvidenceIsSanitized -Manifest $Manifest
    $leaf = Split-Path -Leaf $resolved
    $staging = Join-Path $parent (".$leaf.staging-" + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        $packageRoot = [IO.Directory]::CreateDirectory((Join-Path $staging 'packages')).FullName
        foreach ($package in $Packages) {
            $packageDirectory = Join-Path $packageRoot ([string]$package.PackageAlias)
            [IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
            $infResult = Copy-ExactEvidenceFile `
                -Source ([string]$package.InfPath) `
                -Destination (Join-Path $packageDirectory ([string]$package.OriginalInf)) `
                -AllowedSourceRoot $AllowedSourceRoot
            $catResult = Copy-ExactEvidenceFile `
                -Source ([string]$package.CatalogPath) `
                -Destination (Join-Path $packageDirectory ([string]$package.CatalogFile)) `
                -AllowedSourceRoot $AllowedSourceRoot
            $package.Manifest.inf = $infResult
            $package.Manifest.catalog.size = $catResult.size
            $package.Manifest.catalog.sha256 = $catResult.sha256
        }
        $manifestPath = Join-Path $staging 'manifest.json'
        $manifestJson = ($Manifest | ConvertTo-Json -Depth 20) + "`r`n"
        [IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))
        [IO.Directory]::Move($staging, $resolved)
    }
    catch {
        if ((Test-Path -LiteralPath $staging) -and
            (Test-OwnedStagingPath -Path $staging -Parent $parent -FinalLeaf $leaf)) {
            [IO.Directory]::Delete($staging, $true)
        }
        throw
    }
    return $resolved
}

function Get-LiveHostIdentity {
    $bios = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' -ErrorAction Stop
    $system = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\SystemInformation' -ErrorAction Stop
    return [ordered]@{
        BiosManufacturer = [string]$bios.SystemManufacturer
        BiosProduct = [string]$bios.SystemProductName
        SystemManufacturer = [string]$system.SystemManufacturer
        SystemProduct = [string]$system.SystemProductName
    }
}

function Read-PnpPropertyValue {
    param(
        [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string]$KeyName
    )

    try {
        return (Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $KeyName -ErrorAction Stop).Data
    }
    catch [Microsoft.PowerShell.Cmdletization.Cim.CimJobException] {
        return $null
    }
}

function Get-LiveDeviceRecords {
    param([Parameter(Mandatory)][Collections.IDictionary]$Profile)

    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop)
    if ($devices.Count -gt $script:MaximumDevices) {
        throw 'The present-device inventory exceeds the collector bound.'
    }
    $basic = @(
        foreach ($device in $devices) {
            $instance = [string]$device.InstanceId
            [pscustomobject]@{
                InstanceId = $instance
                HardwareIds = Get-StringArray (Read-PnpPropertyValue $instance 'DEVPKEY_Device_HardwareIds')
                ParentInstanceId = [string](Read-PnpPropertyValue $instance 'DEVPKEY_Device_Parent')
            }
        })
    $codecs = @($basic | Where-Object {
            (Get-StringArray $_.HardwareIds) -contains [string]$Profile.CodecHardwareId
        })
    if ($codecs.Count -ne 1) {
        throw "Expected one exact $($Profile.ModelNumber) codec, found $($codecs.Count)."
    }
    $codecInstance = [string]$codecs[0].InstanceId
    $selected = @($basic | Where-Object {
            [string]::Equals($_.InstanceId, $codecInstance, [StringComparison]::OrdinalIgnoreCase) -or
            ([string]::Equals($_.ParentInstanceId, $codecInstance, [StringComparison]::OrdinalIgnoreCase) -and
                @(Get-StringArray $_.HardwareIds | Where-Object { Test-ThxChildHardwareId $_ }).Count -gt 0)
        })
    return @(
        foreach ($device in $selected) {
            $instance = [string]$device.InstanceId
            [pscustomobject]@{
                InstanceId = $instance
                HardwareIds = $device.HardwareIds
                ParentInstanceId = $device.ParentInstanceId
                PublishedInf = [string](Read-PnpPropertyValue $instance 'DEVPKEY_Device_DriverInfPath')
                DriverSection = [string](Read-PnpPropertyValue $instance 'DEVPKEY_Device_DriverInfSection')
                DriverVersion = [string](Read-PnpPropertyValue $instance 'DEVPKEY_Device_DriverVersion')
                DriverProvider = [string](Read-PnpPropertyValue $instance 'DEVPKEY_Device_DriverProvider')
                DriverKey = [string](Read-PnpPropertyValue $instance 'DEVPKEY_Device_Driver')
                ExtendedConfigurationIds = if ([string]::Equals(
                        $instance,
                        $codecInstance,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    Get-StringArray (Read-PnpPropertyValue $instance 'DEVPKEY_Device_ExtendedConfigurationIds')
                }
                else {
                    @()
                }
            }
        })
}

function Add-SetupApiReaderType {
    if ('OpenBlade.LegacyThx.SetupApiReader' -as [type]) {
        return
    }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.LegacyThx
{
    public static class SetupApiReader
    {
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupGetInfDriverStoreLocation(
            string fileName, IntPtr alternatePlatformInfo, string localeName,
            StringBuilder returnBuffer, uint returnBufferSize, out uint requiredSize);

        public static string Resolve(string publishedInfPath)
        {
            var buffer = new StringBuilder(32768);
            uint required;
            if (!SetupGetInfDriverStoreLocation(
                publishedInfPath, IntPtr.Zero, null, buffer, (uint)buffer.Capacity, out required))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return buffer.ToString();
        }
    }
}
'@
}

function Get-InfCatalogFile {
    param([Parameter(Mandatory)][string]$InfPath)

    $info = Get-Item -LiteralPath $InfPath -Force -ErrorAction Stop
    if ($info.Length -gt 2097152) {
        throw 'The selected INF exceeds the parsing bound.'
    }
    $reader = [IO.StreamReader]::new($InfPath, [Text.Encoding]::Default, $true)
    try {
        $text = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
    $catalogs = @([regex]::Matches(
            $text,
            '(?im)^\s*CatalogFile(?:\.[^=\s]+)?\s*=\s*([^;\r\n]+)') |
            ForEach-Object { $_.Groups[1].Value.Trim().Trim('"') } |
            Sort-Object -Unique)
    if ($catalogs.Count -ne 1 -or
        [IO.Path]::GetFileName($catalogs[0]) -ne $catalogs[0] -or
        -not $catalogs[0].EndsWith('.cat', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The selected INF does not identify one bounded catalog file.'
    }
    return $catalogs[0]
}

function Get-InfText {
    param([Parameter(Mandatory)][string]$InfPath)

    $info = Get-Item -LiteralPath $InfPath -Force -ErrorAction Stop
    if ($info.Length -gt 2097152) {
        throw 'The selected INF exceeds the parsing bound.'
    }
    $reader = [IO.StreamReader]::new($InfPath, [Text.Encoding]::Default, $true)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Test-WindowsRecordedConfigurationIdentity {
    param(
        [Parameter(Mandatory)]$Source,
        [Parameter(Mandatory)][string]$InfText
    )

    return Test-InfPackageSourceIdentity -Source $Source -InfText $InfText -RequireDriverDate
}

function Test-InfPackageSourceIdentity {
    param(
        [Parameter(Mandatory)]$Source,
        [Parameter(Mandatory)][string]$InfText,
        [switch]$RequireDriverDate
    )

    $verificationHardwareIds = @(Get-StringArray (
            Get-OptionalProperty -InputObject $Source -Name 'VerificationHardwareIds'))
    $expectedHardwareIds = if ($verificationHardwareIds.Count -eq 0) {
        @([string]$Source.HardwareId)
    }
    else {
        $verificationHardwareIds
    }
    $expectedSection = [string]$Source.Section
    $expectedVersion = [string]$Source.DriverVersion
    if (@($expectedHardwareIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -eq 0 -or
        [string]::IsNullOrWhiteSpace($expectedSection) -or
        [string]::IsNullOrWhiteSpace($expectedVersion)) {
        return $false
    }

    $bindingFound = $false
    foreach ($line in [regex]::Split($InfText, '\r?\n')) {
        $match = [regex]::Match(
            $line,
            '^\s*[^;=]+\s*=\s*"?([^",\s]+)"?\s*,\s*"?([^",;\s]+)',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) {
            continue
        }
        $mappedHardwareId = $match.Groups[2].Value
        if (@($expectedHardwareIds | Where-Object {
                    [string]::Equals($_, $mappedHardwareId, [StringComparison]::OrdinalIgnoreCase)
                }).Count -eq 0) {
            continue
        }
        $declaredSection = $match.Groups[1].Value
        if ([string]::Equals($declaredSection, $expectedSection, [StringComparison]::OrdinalIgnoreCase) -or
            $expectedSection.StartsWith($declaredSection + '.', [StringComparison]::OrdinalIgnoreCase)) {
            $bindingFound = $true
            break
        }
    }
    if (-not $bindingFound) {
        return $false
    }
    $sectionPattern = '(?im)^\s*\[' + [regex]::Escape($expectedSection) + '\]\s*(?:;.*)?$'
    if (-not [regex]::IsMatch($InfText, $sectionPattern)) {
        return $false
    }
    $datePattern = if ($RequireDriverDate) {
        [regex]::Escape([string]$Source.DriverDate)
    }
    else {
        '[0-9]{1,2}/[0-9]{1,2}/[0-9]{4}'
    }
    $versionPattern = '(?im)^\s*DriverVer\s*=\s*' + $datePattern + '\s*,\s*' +
        [regex]::Escape($expectedVersion) + '\s*(?:;.*)?$'
    return [regex]::IsMatch($InfText, $versionPattern)
}

function Resolve-LiveSelectedPackages {
    param([Parameter(Mandatory)]$Selection)

    Add-SetupApiReaderType
    $resolved = @()
    $rejected = @()
    $index = 0
    foreach ($source in $Selection.PublishedPackages) {
        $index++
        $published = [string]$source.PublishedInf
        $windowsInf = Join-Path $env:WINDIR ('INF\' + $published)
        $infPath = [OpenBlade.LegacyThx.SetupApiReader]::Resolve($windowsInf)
        Assert-NoReparsePointsUnderRoot -Path $infPath -Root $script:DriverStoreRoot
        $originalInf = [IO.Path]::GetFileName($infPath)
        if ($originalInf -notmatch '^[A-Za-z0-9_.-]+[.]inf$') {
            throw 'The selected Driver Store INF has an unsafe original filename.'
        }
        $infText = Get-InfText -InfPath $infPath
        $catalogFile = Get-InfCatalogFile -InfPath $infPath
        if ($catalogFile -notmatch '^[A-Za-z0-9_.-]+[.]cat$') {
            throw 'The selected Driver Store catalog has an unsafe filename.'
        }
        $catalogPath = Join-Path (Split-Path -Parent $infPath) $catalogFile
        Assert-NoReparsePointsUnderRoot -Path $catalogPath -Root $script:DriverStoreRoot
        $sourceManifests = @()
        $recordedMismatch = $false
        $boundMismatch = $false
        $hasVerifiedBoundSource = $false
        foreach ($packageSource in @($Selection.PackageSources | Where-Object {
                    [string]::Equals($_.PublishedInf, $published, [StringComparison]::OrdinalIgnoreCase)
                })) {
            $sourceIdentity = 'NotApplicable'
            if ([string]$packageSource.Source -eq 'BoundDevice') {
                if (Test-InfPackageSourceIdentity -Source $packageSource -InfText $infText) {
                    $sourceIdentity = 'VerifiedAgainstResolvedInf'
                    $hasVerifiedBoundSource = $true
                }
                else {
                    $sourceIdentity = 'SourceIdentityMismatch'
                    $boundMismatch = $true
                }
            }
            elseif ([string]$packageSource.Source -eq 'WindowsRecordedConfiguration') {
                if (Test-WindowsRecordedConfigurationIdentity -Source $packageSource -InfText $infText) {
                    $sourceIdentity = 'VerifiedAgainstResolvedInf'
                }
                else {
                    $sourceIdentity = 'SourceIdentityMismatch'
                    $recordedMismatch = $true
                }
            }
            $sourceManifests += [ordered]@{
                source = [string]$packageSource.Source
                node = [string](Get-OptionalProperty -InputObject $packageSource -Name 'NodeAlias')
                hardwareId = [string]$packageSource.HardwareId
                section = [string]$packageSource.Section
                driverDate = $packageSource.DriverDate
                driverVersion = [string]$packageSource.DriverVersion
                status = [string]$packageSource.Status
                sourceIdentity = $sourceIdentity
            }
        }
        if ($boundMismatch -or ($recordedMismatch -and -not $hasVerifiedBoundSource)) {
            $rejected += [ordered]@{
                originalInf = $originalInf
                sources = $sourceManifests
                status = 'SourceIdentityMismatch'
                copied = $false
            }
            continue
        }
        $alias = 'package-{0:D2}' -f $index
        $manifest = [ordered]@{
            alias = $alias
            originalInf = $originalInf
            sources = $sourceManifests
            inf = [ordered]@{ size = 0; sha256 = '' }
            catalog = [ordered]@{
                file = $catalogFile
                size = 0
                sha256 = ''
                signatureStatus = 'PendingOfflineReview'
                signerSubject = $null
                signerThumbprint = $null
                infMembership = 'PendingOfflineReview'
            }
        }
        $resolved += [pscustomobject]@{
            PackageAlias = $alias
            PublishedInf = $published
            OriginalInf = $originalInf
            CatalogFile = $catalogFile
            InfPath = $infPath
            CatalogPath = $catalogPath
            Section = [string]$source.Section
            Manifest = $manifest
        }
    }
    return [pscustomobject]@{
        Packages = $resolved
        RejectedSources = $rejected
    }
}

function Read-RegistryTree {
    param(
        [Parameter(Mandatory)][Microsoft.Win32.RegistryKey]$Key,
        [Parameter(Mandatory)][string]$EvidencePath,
        [int]$Depth = 0,
        [Collections.IDictionary]$Bounds,
        [bool]$Recurse = $true
    )

    if ($Depth -gt $script:MaximumRegistryDepth) {
        throw 'The selected registry tree exceeds the depth bound.'
    }
    if ($null -eq $Bounds) {
        $Bounds = @{ Nodes = 0; Values = 0 }
    }
    $Bounds.Nodes = [int]$Bounds.Nodes + 1
    if ($Bounds.Nodes -gt 64) {
        throw 'The registry-source tree count exceeds the collector bound.'
    }
    $valueNames = @($Key.GetValueNames() | Sort-Object)
    $Bounds.Values = [int]$Bounds.Values + $valueNames.Count
    if ($Bounds.Values -gt $script:MaximumRegistryValues) {
        throw 'The registry-source value count exceeds the collector bound.'
    }
    $values = @()
    foreach ($name in $valueNames) {
        $kind = [string]$Key.GetValueKind($name)
        $value = $Key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $values += [pscustomobject]@{ Name = $name; Kind = $kind; Value = $value }
    }
    $result = @([pscustomobject]@{ Path = $EvidencePath; Values = $values })
    if (-not $Recurse) {
        return $result
    }
    foreach ($subName in @($Key.GetSubKeyNames() | Sort-Object)) {
        if ($subName.Length -gt 128 -or $subName -notmatch '^[A-Za-z0-9_.{}(), -]+$') {
            throw 'A selected registry subkey is outside the evidence whitelist.'
        }
        $subKey = $Key.OpenSubKey($subName, $false)
        if ($null -eq $subKey) {
            throw 'A selected registry subkey could not be read.'
        }
        try {
            $result += Read-RegistryTree `
                -Key $subKey `
                -EvidencePath ($EvidencePath + '\' + $subName) `
                -Depth ($Depth + 1) `
                -Bounds $Bounds
        }
        finally {
            $subKey.Dispose()
        }
    }
    return $result
}

function Get-LiveRegistryEvidence {
    param([Parameter(Mandatory)][string]$DriverKey)

    if ($DriverKey -notmatch '^\{[0-9A-Fa-f-]{36}\}\\[0-9]{4}$') {
        return [pscustomobject]@{ Status = 'MissingEvidence'; Trees = @() }
    }
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $driver = $machine.OpenSubKey(
            'SYSTEM\CurrentControlSet\Control\Class\' + $DriverKey,
            $false)
        if ($null -eq $driver) {
            return [pscustomobject]@{ Status = 'MissingEvidence'; Trees = @() }
        }
        try {
            $trees = @()
            $bounds = @{ Nodes = 0; Values = 0 }
            if (@($driver.GetValueNames() | Where-Object {
                        [string]::Equals($_, 'InterfaceSetting', [StringComparison]::OrdinalIgnoreCase)
                    }).Count -eq 1) {
                $value = $driver.GetValue(
                    'InterfaceSetting',
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $bounds.Nodes++
                $bounds.Values++
                $trees += [pscustomobject]@{
                    Path = 'InterfaceSetting'
                    Values = @([pscustomobject]@{
                            Name = 'InterfaceSetting'
                            Kind = [string]$driver.GetValueKind('InterfaceSetting')
                            Value = $value
                        })
                }
            }
            $interface = $driver.OpenSubKey('InterfaceSetting', $false)
            if ($null -ne $interface) {
                try {
                    $trees += Read-RegistryTree `
                        -Key $interface `
                        -EvidencePath 'InterfaceSetting' `
                        -Bounds $bounds `
                        -Recurse $false
                }
                finally {
                    $interface.Dispose()
                }
            }
            if ($trees.Count -eq 0) {
                return [pscustomobject]@{ Status = 'MissingEvidence'; Trees = @() }
            }

            $references = [Collections.Generic.List[string]]::new()
            $seenReferences = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $referencedSourceCount = 0
            foreach ($tree in @($trees | Where-Object {
                        [string]::Equals($_.Path, 'InterfaceSetting', [StringComparison]::OrdinalIgnoreCase)
                    })) {
                foreach ($value in @($tree.Values)) {
                    foreach ($candidate in Get-StringArray $value.Value) {
                        if (Test-InterfaceSettingReference -Value $candidate) {
                            if ($seenReferences.Add($candidate)) {
                                $references.Add($candidate)
                            }
                        }
                    }
                }
            }
            foreach ($reference in $references) {
                foreach ($kind in 'FX', 'EP') {
                    $relative = $reference + '\' + $kind + '\0'
                    $source = $driver.OpenSubKey($relative, $false)
                    if ($null -eq $source) {
                        continue
                    }
                    try {
                        $referencedSourceCount++
                        $trees += Read-RegistryTree `
                            -Key $source `
                            -EvidencePath ('ReferencedSource\' + $reference + '\' + $kind + '\0') `
                            -Bounds $bounds
                    }
                    finally {
                        $source.Dispose()
                    }
                }
            }
            return [pscustomobject]@{
                Status = if ($references.Count -eq 0 -or $referencedSourceCount -eq 0) {
                    'PartialMissingReferencedSource'
                }
                else {
                    'EvidenceOnly'
                }
                Trees = ConvertTo-LegacyThxRegistryEvidence -Trees $trees
            }
        }
        finally {
            $driver.Dispose()
        }
    }
    finally {
        $machine.Dispose()
    }
}

function Get-LiveSetupApiFacts {
    param([Parameter(Mandatory)][object[]]$Packages)

    $path = Join-Path $env:WINDIR 'INF\setupapi.dev.log'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return ,@($Packages | ForEach-Object {
                [ordered]@{
                    logOrder = $null
                    packageAlias = $_.PackageAlias
                    originalInf = $_.OriginalInf
                    section = $_.Section
                    time = $null
                    status = 'MissingEvidence'
                    source = 'SetupApiDevLog'
                }
            })
    }
    $info = Get-Item -LiteralPath $path -Force
    if ($info.Length -gt $script:MaximumSetupLogBytes) {
        return ,@($Packages | ForEach-Object {
                [ordered]@{
                    logOrder = $null
                    packageAlias = $_.PackageAlias
                    originalInf = $_.OriginalInf
                    section = $_.Section
                    time = $null
                    status = 'MissingEvidenceLogTooLarge'
                    source = 'SetupApiDevLog'
                }
            })
    }
    $lines = [IO.File]::ReadAllLines($path)
    $factPackages = @($Packages | ForEach-Object {
            [pscustomobject]@{
                PublishedInf = $_.PublishedInf
                PackageAlias = $_.PackageAlias
                OriginalInf = $_.OriginalInf
                Section = $_.Section
            }
        })
    return Get-SanitizedSetupApiFacts -Lines $lines -Packages $factPackages
}

function ConvertTo-PublicRecordedConfigurations {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$RecordedConfigurations,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Packages
    )

    $output = foreach ($source in $RecordedConfigurations) {
        $package = $Packages | Where-Object {
            [string]::Equals(
                $_.PublishedInf,
                $source.PublishedInf,
                [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        $verifiedPackageSource = if ($null -eq $package) {
            $null
        }
        else {
            $package.Manifest.sources | Where-Object {
                [string]::Equals($_.source, 'WindowsRecordedConfiguration', [StringComparison]::Ordinal) -and
                [string]::Equals($_.hardwareId, $source.HardwareId, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals($_.section, $source.Section, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string]$_.driverDate, [string]$source.DriverDate, [StringComparison]::Ordinal) -and
                [string]::Equals($_.driverVersion, $source.DriverVersion, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals($_.sourceIdentity, 'VerifiedAgainstResolvedInf', [StringComparison]::Ordinal)
            } | Select-Object -First 1
        }
        [ordered]@{
            packageAlias = if ($null -eq $verifiedPackageSource) { $null } else { $package.PackageAlias }
            hardwareId = $source.HardwareId
            section = $source.Section
            driverDate = $source.DriverDate
            driverVersion = $source.DriverVersion
            source = $source.Source
            status = $source.Status
            sourceIdentity = if ($null -eq $verifiedPackageSource) {
                'SourceIdentityMismatch'
            }
            else {
                'VerifiedAgainstResolvedInf'
            }
        }
    }
    return ,@($output)
}

function Invoke-LegacyThxBindingEvidenceExport {
    param(
        [Parameter(Mandatory)][string]$RequestedModel,
        [Parameter(Mandatory)][string]$RequestedOutputDirectory
    )

    if (($PSVersionTable.PSEdition -ne 'Desktop') -or
        ($PSVersionTable.PSVersion -lt [version]'5.1')) {
        throw 'The collector requires Windows PowerShell 5.1.'
    }
    $profile = Get-LegacyThxProfile -ModelNumber $RequestedModel
    $hostIdentity = Get-LiveHostIdentity
    if (-not (Test-ExactHostIdentity -Profile $profile -Identity $hostIdentity)) {
        throw "The two Windows identity sources do not confirm exact host $RequestedModel."
    }
    $devices = @(Get-LiveDeviceRecords -Profile $profile)
    $selection = Select-LegacyThxBindingSource `
        -ModelNumber $RequestedModel `
        -HostIdentity $hostIdentity `
        -Devices $devices
    $resolution = Resolve-LiveSelectedPackages -Selection $selection
    $packages = @($resolution.Packages)
    $registry = Get-LiveRegistryEvidence -DriverKey $selection.CodecDriverKey
    $provenance = Get-LiveSetupApiFacts -Packages $packages

    $manifest = [ordered]@{
        manifestType = 'OpenBlade.LegacyThxBindingEvidence'
        schemaVersion = 1
        classification = 'PrivateDeveloperEvidence'
        conclusion = 'Partial'
        modelNumber = $RequestedModel
        codecHardwareId = $selection.Profile.CodecHardwareId
        collectedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        activeEndpointAssociation = 'MissingEvidence'
        limitations = @(
            'WindowsRecordedConfiguration entries are historical Windows records, not proof of successful application.',
            'No active MMDevice endpoint association was collected.',
            'This evidence does not establish installability or successful THX processing.'
        )
        nodes = $selection.Nodes
        recordedConfigurations = ConvertTo-PublicRecordedConfigurations `
            -RecordedConfigurations @($selection.RecordedConfigurations) `
            -Packages $packages
        registryEvidence = [ordered]@{
            status = $registry.Status
            trees = $registry.Trees
        }
        setupApiProvenance = $provenance
        sourceIdentityIssues = $resolution.RejectedSources
        packages = @($packages | ForEach-Object { $_.Manifest })
        privacy = [ordered]@{
            containsSerials = $false
            containsUserPaths = $false
            containsDeviceInstanceIds = $false
            containsInterfaceOrEndpointIds = $false
            containsMachineLocalPublishedInf = $false
            containsRawSetupApiLog = $false
        }
    }
    $published = Publish-LegacyThxBindingEvidence `
        -Destination $RequestedOutputDirectory `
        -Manifest $manifest `
        -Packages $packages `
        -AllowedSourceRoot $script:DriverStoreRoot
    return [pscustomobject]@{
        OutputDirectory = $published
        Conclusion = $manifest.conclusion
        PackageCount = $packages.Count
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($Model)) {
        throw 'Model is required.'
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        throw 'OutputDirectory is required.'
    }
    Invoke-LegacyThxBindingEvidenceExport `
        -RequestedModel $Model `
        -RequestedOutputDirectory $OutputDirectory
}
