[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\StoreSubmissionValidation.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessage
    )

    $caught = $null
    try {
        & $Action
    }
    catch {
        $caught = $_.Exception
    }

    Assert-True ($null -ne $caught) "Expected failure containing '$ExpectedMessage'."
    Assert-True ($caught.Message.Contains($ExpectedMessage)) `
        "Expected '$ExpectedMessage' in '$($caught.Message)'."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade.StoreSubmissionValidationTests.' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $testRoot 'OpenBlade'
$payload = Join-Path $bundle 'payload'
$installer = Join-Path $testRoot 'OpenBlade-Setup.exe'
$reportPath = Join-Path $testRoot 'store-readiness.json'
$unsignedFixture = Join-Path $testRoot 'OpenBlade.UnsignedFixture.dll'
$invalidContract = Join-Path $testRoot 'invalid-store-contract.json'
$renderedPrivacyPolicy = @'
<!doctype html>
<html><body><h1 class="type-page-title">Your data stays on your PC</h1></body></html>
'@
$privacyPolicyRequest = {
    param([Uri]$Uri)

    if ($Uri.AbsoluteUri -ne 'https://openblade.sh/privacy.html') {
        throw "Unexpected privacy-policy URL: $Uri"
    }

    return [pscustomobject]@{
        StatusCode = 200
        Headers = @{
            'Content-Type' = 'text/html; charset=utf-8'
        }
        Content = $renderedPrivacyPolicy
    }
}

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Add-Type -TypeDefinition 'public sealed class OpenBladeUnsignedFixture { }' `
        -OutputAssembly $unsignedFixture
    Copy-Item -LiteralPath $unsignedFixture `
        -Destination (Join-Path $bundle 'OpenBlade.Setup.exe')
    Copy-Item -LiteralPath $unsignedFixture `
        -Destination (Join-Path $payload 'OpenBlade.Service.exe')
    $nestedFirstPartyDirectory = Join-Path $payload 'runtimes\win-x64\native'
    New-Item -ItemType Directory -Path $nestedFirstPartyDirectory -Force | Out-Null
    Copy-Item -LiteralPath $unsignedFixture `
        -Destination (Join-Path $nestedFirstPartyDirectory 'OpenBlade.Native.dll')
    Copy-Item -LiteralPath $unsignedFixture -Destination $installer

    $report = Write-OpenBladeStoreReadinessReport `
        -Bundle $bundle `
        -Installer $installer `
        -ReportPath $reportPath

    Assert-True $report.localPreflightPassed 'The unsigned local preflight should pass.'
    Assert-True (-not $report.signingComplete) 'Signing must remain visibly incomplete.'
    Assert-True (-not $report.readyForPartnerCenterSubmission) `
        'A local report must not claim that external submission is complete.'
    Assert-True ($report.signatureSummary.portableExecutableCount -eq 4) `
        'Every fixture PE must be inventoried.'
    Assert-True ($report.signatureSummary.unsignedFirstPartyCount -eq 4) `
        'Every unsigned first-party fixture must be reported.'
    Assert-True (
        $report.storeContract.privacyPolicyUrl -eq
            'https://openblade.sh/privacy.html') `
        'The readiness report must carry the absolute privacy-policy URL.'
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) `
        'The Store readiness JSON report was not written.'

    $verifiedReport = Write-OpenBladeStoreReadinessReport `
        -Bundle $bundle `
        -Installer $installer `
        -ReportPath $reportPath `
        -VerifyPrivacyPolicy `
        -PrivacyPolicyRequest $privacyPolicyRequest
    Assert-True $verifiedReport.localPreflightPassed `
        'A rendered canonical privacy policy must pass the release preflight.'

    $localDevelopmentArtifact = Join-Path $bundle 'OpenBlade.LocalDevDeployment.json'
    Set-Content -LiteralPath $localDevelopmentArtifact -Value '{}' -Encoding UTF8
    Assert-Throws {
        Write-OpenBladeStoreReadinessReport `
            -Bundle $bundle `
            -Installer $installer `
            -ReportPath $reportPath | Out-Null
    } 'Store submission contains local-development deployment artifact'
    Remove-Item -LiteralPath $localDevelopmentArtifact

    Assert-Throws {
        Assert-OpenBladePrivacyPolicyResponse `
            -StatusCode 404 `
            -ContentType 'text/html; charset=utf-8' `
            -Content $renderedPrivacyPolicy
    } 'did not return HTTP 200'
    Assert-Throws {
        Assert-OpenBladePrivacyPolicyResponse `
            -StatusCode 200 `
            -ContentType 'text/plain' `
            -Content $renderedPrivacyPolicy
    } 'did not return text/html'
    Assert-Throws {
        Assert-OpenBladePrivacyPolicyResponse `
            -StatusCode 200 `
            -ContentType 'text/html; charset=utf-8' `
            -Content '<pre>&lt;h1&gt;Your data stays on your PC&lt;/h1&gt;</pre>'
    } 'does not contain the rendered OpenBlade privacy heading'

    Assert-Throws {
        Write-OpenBladeStoreReadinessReport `
            -Bundle $bundle `
            -Installer $installer `
            -ReportPath $reportPath `
            -RequireSignatures | Out-Null
    } 'Store submission requires signatures'

    $thirdParty = Join-Path $nestedFirstPartyDirectory 'Unexpected.Dependency.dll'
    Copy-Item -LiteralPath $unsignedFixture -Destination $thirdParty
    Assert-Throws {
        Write-OpenBladeStoreReadinessReport `
            -Bundle $bundle `
            -Installer $installer `
            -ReportPath $reportPath | Out-Null
    } 'Third-party portable executables are unsigned'

    $contract = Get-Content -LiteralPath (
        Join-Path $repository 'build\store\OpenBlade.StoreSubmission.json') -Raw |
        ConvertFrom-Json
    $contract.privacyPolicyUrl = 'privacy.html'
    $contract | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $invalidContract -Encoding UTF8
    Assert-Throws {
        Get-OpenBladeStoreSubmissionContract -ContractPath $invalidContract | Out-Null
    } 'Store submission contract is invalid'

    $contract = Get-Content -LiteralPath (
        Join-Path $repository 'build\store\OpenBlade.StoreSubmission.json') -Raw |
        ConvertFrom-Json
    $contract.minimumOperatingSystem = 'Windows 11 x64'
    $contract | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $invalidContract -Encoding UTF8
    Assert-Throws {
        Get-OpenBladeStoreSubmissionContract -ContractPath $invalidContract | Out-Null
    } 'Store submission contract is invalid'
}
finally {
    $canonicalTestRoot = [IO.Path]::GetFullPath($testRoot)
    $canonicalTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $canonicalTestRoot.StartsWith(
            $canonicalTemp + 'OpenBlade.StoreSubmissionValidationTests.',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected Store validation test path: $canonicalTestRoot"
    }
    if (Test-Path -LiteralPath $canonicalTestRoot) {
        Remove-Item -LiteralPath $canonicalTestRoot -Recurse -Force
    }
}

Write-Host 'Store submission validation tests passed.'
