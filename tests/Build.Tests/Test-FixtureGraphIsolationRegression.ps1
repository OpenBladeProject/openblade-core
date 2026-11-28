#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvaluationsPath, [Parameter(Mandatory)][string]$ScratchRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FixtureGraphIsolation.ps1')
$repository = Split-Path (Split-Path $PSScriptRoot)
Assert-ExternalFixtureEvidenceRoot $repository $ScratchRoot
if (Test-Path -LiteralPath $ScratchRoot) { throw 'Use a fresh private regression directory.' }
[IO.Directory]::CreateDirectory($ScratchRoot) | Out-Null
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $errorCaught = $null
    try { $null = & $Action } catch { $errorCaught = $_ }
    if (-not $errorCaught -or $errorCaught.Exception.Message -notlike "*$Message*") { throw "Expected rejection: $Message; got $errorCaught" }
}
$rows = @(Get-Content -LiteralPath $EvaluationsPath -Raw | ConvertFrom-Json)
Assert-FixtureGraphIsolation $rows
Write-Output "PASS: actual $($rows.Count)-row graph passes the real validator."
foreach ($project in @('OpenBlade.DeploymentBroker','OpenBlade.Deployment.Tests')) {
    foreach ($layout in @('ordinary','artifacts')) {
        $group = @($rows | Where-Object { $_.project -eq $project -and $_.layout -eq $layout })
        if ($group.Count -lt 2 -or ($group | Where-Object variant -eq normal)) { throw "Expected actual fixture-only group: $project $layout" }
        foreach ($field in @('target','intermediate','extensions','assets','output','identity')) {
            $copy = @($rows | ConvertTo-Json -Depth 5 | ConvertFrom-Json)
            $fixture = @($copy | Where-Object { $_.project -eq $project -and $_.layout -eq $layout })
            $fixture[1].$field = $fixture[0].$field
            Assert-Rejected { Assert-FixtureGraphIsolation $copy } "Variant collision: $field"
            Write-Output "PASS: fixture-only $project $layout rejects colliding $field."
        }
    }
}
$missing = @($rows | Where-Object { -not ($_.project -eq 'OpenBlade.Service' -and $_.variant -eq 'full' -and $_.layout -eq 'ordinary') })
Assert-Rejected { Assert-FixtureGraphIsolation $missing } 'Missing reachable OpenBlade.Service'
$duplicate = @($rows | ConvertTo-Json -Depth 5 | ConvertFrom-Json)
$agreed = @(Get-AgreedFixtureGraphRows @($rows + $duplicate))
if ($agreed.Count -ne $rows.Count) { throw 'Agreed roots did not deduplicate.' }
foreach ($field in @('target','intermediate','extensions','assets','identity','output','defines','references')) {
    $different = @($rows[0] | ConvertTo-Json | ConvertFrom-Json)
    $different[0].$field += '-different-root'
    Assert-Rejected { Get-AgreedFixtureGraphRows @($rows + $different) } "Root context disagreement: $field"
}
Write-Output 'PASS: full presence and all eight repeated-root agreement fields.'
# Run the actual entry guard in a private repository-shaped replica. A local
# dotnet function stops valid external execution at the first evaluation.
$replica = Join-Path $ScratchRoot 'source'
$tests = Join-Path $replica 'tests/Build.Tests'
[IO.Directory]::CreateDirectory($tests) | Out-Null
foreach ($file in @('Test-FixtureGraphIsolation.ps1','FixtureGraphIsolation.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $tests
}
$sentinel = Join-Path $replica 'sentinel.txt'
Set-Content -LiteralPath $sentinel -Value 'preserve source'
$before = @(Get-ChildItem -LiteralPath $replica -File -Recurse | Get-FileHash | ForEach-Object { "$($_.Path)|$($_.Hash)" })
$script = Join-Path $tests 'Test-FixtureGraphIsolation.ps1'
foreach ($root in @($replica, ($replica + '\'), ($replica + '\\'), $replica.ToUpperInvariant(),
    (Join-Path $replica 'nested'), (Join-Path $replica 'nested/../child/'))) {
    Assert-Rejected { & $script -EvidenceRoot $root } 'Use an external private evaluation directory.'
    $after = @(Get-ChildItem -LiteralPath $replica -File -Recurse | Get-FileHash | ForEach-Object { "$($_.Path)|$($_.Hash)" })
    if (Compare-Object $before $after) { throw 'Rejected evidence root changed source.' }
    if (@(Get-ChildItem -LiteralPath $replica -Directory).Count -ne 1) { throw 'Rejected root created a source directory.' }
}
function dotnet { throw 'Private evaluation boundary reached' }
$external = Join-Path $ScratchRoot 'external'
Assert-Rejected { & $script -EvidenceRoot $external } 'Private evaluation boundary reached'
if (-not (Test-Path -LiteralPath (Join-Path $external 'Snapshot.targets'))) { throw 'Valid external root did not reach evaluation.' }
$after = @(Get-ChildItem -LiteralPath $replica -File -Recurse | Get-FileHash | ForEach-Object { "$($_.Path)|$($_.Hash)" })
if (Compare-Object $before $after) { throw 'Valid external evaluation changed source.' }
Write-Output 'PASS: actual guard rejects equality/trailing/case/descendants before writes; valid external root reaches evaluation; source/sentinel intact.'
