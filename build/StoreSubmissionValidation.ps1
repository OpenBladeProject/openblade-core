function Get-OpenBladeStoreSubmissionContract {
    param(
        [string]$ContractPath = (
            Join-Path $PSScriptRoot 'store\OpenBlade.StoreSubmission.json')
    )

    if (-not (Test-Path -LiteralPath $ContractPath -PathType Leaf)) {
        throw "The OpenBlade Store submission contract is missing: $ContractPath"
    }

    $contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
    if (($contract.schemaVersion -ne 1) -or
        ($contract.appType -ne 'EXE') -or
        ($contract.architecture -ne 'x64') -or
        ($contract.language -ne 'en-US') -or
        ($contract.installerParameters -ne '/quiet /norestart') -or
        ($contract.offlineInstaller -ne $true) -or
        ($contract.requiresElevation -ne $true) -or
        [string]::IsNullOrWhiteSpace($contract.minimumOperatingSystem) -or
        [string]::IsNullOrWhiteSpace($contract.supportedHardware) -or
        ($contract.privacyPolicyRelativeUrl -ne 'privacy.html')) {
        throw "The OpenBlade Store submission contract is invalid."
    }

    return $contract
}

function Write-OpenBladeStoreReadinessReport {
    param(
        [Parameter(Mandatory)][string]$Bundle,
        [Parameter(Mandatory)][string]$Installer,
        [Parameter(Mandatory)][string]$ReportPath,
        [string]$ContractPath = (
            Join-Path $PSScriptRoot 'store\OpenBlade.StoreSubmission.json'),
        [switch]$RequireSignatures
    )

    $contract = Get-OpenBladeStoreSubmissionContract -ContractPath $ContractPath
    if (-not (Test-Path -LiteralPath $Bundle -PathType Container)) {
        throw "The OpenBlade bundle is missing: $Bundle"
    }
    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) {
        throw "The OpenBlade installer is missing: $Installer"
    }

    $bundlePath = (Resolve-Path -LiteralPath $Bundle).Path.TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    $installerPath = (Resolve-Path -LiteralPath $Installer).Path
    if (-not [string]::Equals(
            [IO.Path]::GetFileName($installerPath),
            'OpenBlade-Setup.exe',
            [StringComparison]::Ordinal)) {
        throw "The Store installer must be named OpenBlade-Setup.exe."
    }

    $bundlePrefix = $bundlePath + [IO.Path]::DirectorySeparatorChar
    $peExtensions = @('.dll', '.exe', '.ocx', '.sys')
    $files = @(Get-ChildItem -LiteralPath $bundlePath -Recurse -File |
        Where-Object { $peExtensions -contains $_.Extension.ToLowerInvariant() })
    if ($files.Count -eq 0) {
        throw "The OpenBlade bundle contains no portable executable files."
    }

    $signatureEntries = [Collections.Generic.List[object]]::new()
    foreach ($file in @($files) + @(Get-Item -LiteralPath $installerPath)) {
        $relativePath = if ($file.FullName.StartsWith(
                $bundlePrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            $file.FullName.Substring($bundlePrefix.Length)
        }
        else {
            $file.Name
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        $status = [string]$signature.Status
        $publisher = if ($null -eq $signature.SignerCertificate) {
            $null
        }
        else {
            $signature.SignerCertificate.Subject
        }
        $isFirstParty = ($relativePath -eq 'OpenBlade-Setup.exe') -or
            ($relativePath -match '^(?:payload\\)?OpenBlade\.[^\\]+\.(?:dll|exe)$')

        if (($status -ne 'Valid') -and ($status -ne 'NotSigned')) {
            throw "Portable executable '$relativePath' has invalid signature status '$status'."
        }
        if (($status -eq 'NotSigned') -and -not $isFirstParty) {
            throw "Third-party portable executable '$relativePath' is unsigned."
        }

        $signatureEntries.Add([ordered]@{
                path = $relativePath
                status = $status
                publisher = $publisher
                firstParty = $isFirstParty
            })
    }

    $orderedEntries = @($signatureEntries | Sort-Object path)
    $unsignedFirstParty = @($orderedEntries |
        Where-Object { $_.status -eq 'NotSigned' } |
        ForEach-Object { $_.path })
    if ($RequireSignatures -and $unsignedFirstParty.Count -gt 0) {
        throw "Store submission requires signatures. Unsigned first-party files: " `
            + ($unsignedFirstParty -join ', ')
    }

    $reportDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
    if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
        throw "The Store readiness report directory is missing: $reportDirectory"
    }

    $report = [ordered]@{
        schemaVersion = 1
        localPreflightPassed = $true
        signingComplete = ($unsignedFirstParty.Count -eq 0)
        readyForPartnerCenterSubmission = $false
        storeContract = [ordered]@{
            appType = $contract.appType
            architecture = $contract.architecture
            language = $contract.language
            installerParameters = $contract.installerParameters
            offlineInstaller = $contract.offlineInstaller
            requiresElevation = $contract.requiresElevation
            minimumOperatingSystem = $contract.minimumOperatingSystem
            supportedHardware = $contract.supportedHardware
            privacyPolicyRelativeUrl = $contract.privacyPolicyRelativeUrl
        }
        signatureSummary = [ordered]@{
            portableExecutableCount = $orderedEntries.Count
            validSignatureCount = @(
                $orderedEntries | Where-Object { $_.status -eq 'Valid' }).Count
            unsignedFirstPartyCount = $unsignedFirstParty.Count
            unsignedThirdPartyCount = 0
        }
        unsignedFirstParty = $unsignedFirstParty
        remainingExternalSteps = @(
            'Authenticode-sign every unsigned first-party PE and the final installer.',
            'Publish the immutable installer at a versioned HTTPS URL.',
            'Deploy privacy.html and enter its public URL in Partner Center.',
            'Complete Microsoft Partner Center certification and hardware disclosures.'
        )
    }

    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $ReportPath -Encoding UTF8
    return $report
}
