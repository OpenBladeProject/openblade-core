[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\PublishPayloadValidation.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-ValidServicePayload {
    param([Parameter(Mandatory)][string]$Payload)

    New-Item -ItemType Directory -Path $Payload -Force | Out-Null
    $targetName = '.NETCoreApp,Version=v10.0/win-x64'
    $manifest = [ordered]@{
        runtimeTarget = [ordered]@{ name = $targetName }
        targets = [ordered]@{
            $targetName = [ordered]@{
                'Microsoft.Extensions.Hosting/10.0.9' = [ordered]@{
                    runtime = [ordered]@{
                        'lib/net10.0/Microsoft.Extensions.Hosting.dll' = [ordered]@{}
                    }
                }
                'Microsoft.Extensions.Hosting.WindowsServices/10.0.9' = [ordered]@{
                    runtime = [ordered]@{
                        'lib/net10.0/Microsoft.Extensions.Hosting.WindowsServices.dll' = [ordered]@{}
                    }
                }
            }
        }
        libraries = [ordered]@{
            'Microsoft.Extensions.Hosting/10.0.9' = [ordered]@{ type = 'package' }
            'Microsoft.Extensions.Hosting.WindowsServices/10.0.9' = [ordered]@{ type = 'package' }
        }
    }

    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Payload 'OpenBlade.Service.deps.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Payload 'Microsoft.Extensions.Hosting.dll') -Value 'hosting' -NoNewline
    Set-Content -LiteralPath (Join-Path $Payload 'Microsoft.Extensions.Hosting.WindowsServices.dll') -Value 'windows-services' -NoNewline
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.PublishValidation.Tests.' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    $existingBundle = Join-Path $testRoot 'existing\OpenBlade'
    $incompleteStaging = Join-Path $testRoot 'incomplete\OpenBlade'
    New-Item -ItemType Directory -Path $existingBundle -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $incompleteStaging 'payload') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $existingBundle 'sentinel.txt') -Value 'previous bundle' -NoNewline
    Set-Content -LiteralPath (Join-Path $incompleteStaging 'payload\OpenBlade.Service.deps.json') -Value '{}' -NoNewline

    $rejected = $false
    try {
        Complete-ValidatedBundleReplacement -StagingBundle $incompleteStaging -Bundle $existingBundle
    }
    catch {
        $rejected = $true
    }

    Assert-True $rejected 'An incomplete service dependency manifest was accepted.'
    Assert-True (Test-Path -LiteralPath (Join-Path $existingBundle 'sentinel.txt')) 'The prior bundle was removed after validation failed.'
    Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $existingBundle 'sentinel.txt')) -eq 'previous bundle') 'The prior bundle changed after validation failed.'

    $validStaging = Join-Path $testRoot 'valid\OpenBlade'
    Write-ValidServicePayload -Payload (Join-Path $validStaging 'payload')
    Set-Content -LiteralPath (Join-Path $validStaging 'replacement.txt') -Value 'validated bundle' -NoNewline

    Complete-ValidatedBundleReplacement -StagingBundle $validStaging -Bundle $existingBundle

    Assert-True (Test-Path -LiteralPath (Join-Path $existingBundle 'replacement.txt')) 'A valid staged bundle was not installed.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $existingBundle 'sentinel.txt'))) 'The prior bundle was not replaced after validation succeeded.'

    Write-Host 'Publish payload validation tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
