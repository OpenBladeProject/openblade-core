Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Get-OpenBladeStoreSubmissionContract {
    param(
        [string]$ContractPath = (
            Join-Path $PSScriptRoot 'store\OpenBlade.StoreSubmission.json')
    )

    if (-not (Test-Path -LiteralPath $ContractPath -PathType Leaf)) {
        throw "The OpenBlade Store submission contract is missing: $ContractPath"
    }

    $contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
    $privacyPolicyUri = $null
    $privacyPolicyValid = [Uri]::TryCreate(
        [string]$contract.privacyPolicyUrl,
        [UriKind]::Absolute,
        [ref]$privacyPolicyUri)
    if (($contract.schemaVersion -ne 1) -or
        ($contract.appType -ne 'EXE') -or
        ($contract.architecture -ne 'x64') -or
        ($contract.language -ne 'en-US') -or
        ($contract.installerParameters -ne '/install /quiet /norestart') -or
        ($contract.offlineInstaller -ne $true) -or
        ($contract.requiresElevation -ne $true) -or
        ($contract.minimumOperatingSystem -ne
            'Windows 11 version 24H2 x64 (build 26100 or later)') -or
        [string]::IsNullOrWhiteSpace($contract.supportedHardware) -or
        (-not $privacyPolicyValid) -or
        ($privacyPolicyUri.Scheme -ne [Uri]::UriSchemeHttps) -or
        ($privacyPolicyUri.AbsoluteUri -ne
            'https://openblade.sh/privacy.html')) {
        throw "The OpenBlade Store submission contract is invalid."
    }

    return $contract
}

function Assert-NoOpenBladeLocalDevelopmentArtifacts {
    param(
        [Parameter(Mandatory)][string]$Bundle,
        [Parameter(Mandatory)][string]$Installer
    )

    $files = @((Get-Item -LiteralPath $Installer))
    $files += @(Get-ChildItem -LiteralPath $Bundle -Recurse -File -Force)
    foreach ($file in $files) {
        if ($file.Name -like 'OpenBlade.DeploymentBroker.*' -or
            $file.Name -like 'OpenBlade.DeploymentClient.*' -or
            $file.Name -like 'OpenBlade.DevelopmentDeployment.Setup.*' -or
            $file.Name -like 'OpenBlade.LocalDevDeployment.Tool.*' -or
            $file.Name -eq 'OpenBlade.Deployment.dll' -or
            $file.Name -in @(
                'OpenBlade.LocalDevDeployment.json',
                'OpenBlade-Local-Dev-Deployment.json',
                'OpenBlade-Local-Dev-Deployment.sig')) {
            throw "Store submission contains local-development deployment artifact '$($file.FullName)'."
        }
    }
}

function Assert-OpenBladePrivacyPolicyResponse {
    param(
        [Parameter(Mandatory)][int]$StatusCode,
        [Parameter(Mandatory)][string]$ContentType,
        [Parameter(Mandatory)][string]$Content
    )

    if ($StatusCode -ne 200) {
        throw "The OpenBlade privacy-policy URL did not return HTTP 200."
    }

    $mediaType = ($ContentType -split ';', 2)[0].Trim()
    if (-not [string]::Equals(
            $mediaType,
            'text/html',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The OpenBlade privacy-policy URL did not return text/html."
    }

    if ($Content -notmatch
        '(?is)<h1\b[^>]*>\s*Your data stays on your PC\s*</h1>') {
        throw "The OpenBlade privacy-policy URL does not contain the rendered OpenBlade privacy heading."
    }
}

function Assert-OpenBladePrivacyPolicyEndpoint {
    param(
        [Parameter(Mandatory)][Uri]$Uri,
        [scriptblock]$Request
    )

    $response = if ($null -eq $Request) {
        Invoke-WebRequest -Uri $Uri -UseBasicParsing
    }
    else {
        & $Request $Uri
    }

    $contentType = [string]$response.Headers['Content-Type']
    Assert-OpenBladePrivacyPolicyResponse `
        -StatusCode ([int]$response.StatusCode) `
        -ContentType $contentType `
        -Content ([string]$response.Content)
}

function Write-OpenBladeStoreReadinessReport {
    param(
        [Parameter(Mandatory)][string]$Bundle,
        [Parameter(Mandatory)][string]$Installer,
        [Parameter(Mandatory)][string]$ReportPath,
        [string]$ContractPath = (
            Join-Path $PSScriptRoot 'store\OpenBlade.StoreSubmission.json'),
        [switch]$RequireSignatures,
        [switch]$VerifyPrivacyPolicy,
        [scriptblock]$PrivacyPolicyRequest
    )

    $contract = Get-OpenBladeStoreSubmissionContract -ContractPath $ContractPath
    if ($VerifyPrivacyPolicy) {
        Assert-OpenBladePrivacyPolicyEndpoint `
            -Uri ([Uri]$contract.privacyPolicyUrl) `
            -Request $PrivacyPolicyRequest
    }
    if (-not (Test-Path -LiteralPath $Bundle -PathType Container)) {
        throw "The OpenBlade bundle is missing: $Bundle"
    }
    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) {
        throw "The OpenBlade installer is missing: $Installer"
    }
    Assert-NoOpenBladeLocalDevelopmentArtifacts -Bundle $Bundle -Installer $Installer

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
    $unsignedThirdParty = [Collections.Generic.List[string]]::new()
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
            ($relativePath -match '^(?:[^\\]+\\)*OpenBlade\.[^\\]+\.(?:dll|exe)$')

        if (($status -ne 'Valid') -and ($status -ne 'NotSigned')) {
            throw "Portable executable '$relativePath' has invalid signature status '$status'."
        }
        if (($status -eq 'NotSigned') -and -not $isFirstParty) {
            $unsignedThirdParty.Add($relativePath)
        }

        $signatureEntries.Add([ordered]@{
                path = $relativePath
                status = $status
                publisher = $publisher
                firstParty = $isFirstParty
            })
    }

    $orderedEntries = @($signatureEntries | Sort-Object path)
    if ($unsignedThirdParty.Count -gt 0) {
        throw "Third-party portable executables are unsigned: " `
            + (@($unsignedThirdParty | Sort-Object) -join ', ')
    }
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
            privacyPolicyUrl = $contract.privacyPolicyUrl
        }
        signatureSummary = [ordered]@{
            portableExecutableCount = $orderedEntries.Count
            validSignatureCount = @(
                $orderedEntries | Where-Object { $_.status -eq 'Valid' }).Count
            unsignedFirstPartyCount = $unsignedFirstParty.Count
            unsignedThirdPartyCount = $unsignedThirdParty.Count
        }
        unsignedFirstParty = $unsignedFirstParty
        remainingExternalSteps = @(
            'Authenticode-sign first-party payloads before Burn embeds them, then sign the detached Burn engine and final installer.',
            'Publish the immutable installer at a versioned HTTPS URL.',
            'Confirm the privacy-policy URL returns HTTP 200 text/html with the rendered OpenBlade privacy heading, then enter it in Partner Center.',
            'Complete Microsoft Partner Center certification and hardware disclosures.'
        )
    }

    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $ReportPath -Encoding UTF8
    return $report
}
