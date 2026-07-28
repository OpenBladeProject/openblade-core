[CmdletBinding()]
param()

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

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Add-Type -TypeDefinition 'public sealed class OpenBladeUnsignedFixture { }' `
        -OutputAssembly $unsignedFixture
    Copy-Item -LiteralPath $unsignedFixture `
        -Destination (Join-Path $bundle 'OpenBlade.Setup.exe')
    Copy-Item -LiteralPath $unsignedFixture `
        -Destination (Join-Path $payload 'OpenBlade.Service.exe')
    Copy-Item -LiteralPath $unsignedFixture -Destination $installer

    $report = Write-OpenBladeStoreReadinessReport `
        -Bundle $bundle `
        -Installer $installer `
        -ReportPath $reportPath

    Assert-True $report.localPreflightPassed 'The unsigned local preflight should pass.'
    Assert-True (-not $report.signingComplete) 'Signing must remain visibly incomplete.'
    Assert-True (-not $report.readyForPartnerCenterSubmission) `
        'A local report must not claim that external submission is complete.'
    Assert-True ($report.signatureSummary.portableExecutableCount -eq 3) `
        'Every fixture PE must be inventoried.'
    Assert-True ($report.signatureSummary.unsignedFirstPartyCount -eq 3) `
        'Every unsigned first-party fixture must be reported.'
    Assert-True (Test-Path -LiteralPath $reportPath -PathType Leaf) `
        'The Store readiness JSON report was not written.'

    Assert-Throws {
        Write-OpenBladeStoreReadinessReport `
            -Bundle $bundle `
            -Installer $installer `
            -ReportPath $reportPath `
            -RequireSignatures | Out-Null
    } 'Store submission requires signatures'

    $thirdParty = Join-Path $payload 'Unexpected.Dependency.dll'
    Copy-Item -LiteralPath $unsignedFixture -Destination $thirdParty
    Assert-Throws {
        Write-OpenBladeStoreReadinessReport `
            -Bundle $bundle `
            -Installer $installer `
            -ReportPath $reportPath | Out-Null
    } 'Third-party portable executable'
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
