[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\PullRequestVersion.ps1')

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', received '$Actual'."
    }
}

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    $thrown = $false
    try {
        & $Action
    }
    catch {
        $thrown = $true
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Message Expected '$Pattern', received '$($_.Exception.Message)'."
        }
    }
    if (-not $thrown) {
        throw "$Message Expected an exception."
    }
}

function New-VersionBody {
    param(
        [Parameter(Mandatory)][string]$Impact,
        [Parameter(Mandatory)][string]$Change,
        [string]$Reason = 'Explains the product version decision.'
    )

    return @"
## Versioning

Version impact: $Impact
Version change: $Change
Reason: $Reason
"@
}

$properties = @'
<Project>
  <PropertyGroup>
    <OpenBladeVersion>1.4.2</OpenBladeVersion>
  </PropertyGroup>
</Project>
'@
Assert-Equal '1.4.2' `
    (Get-OpenBladeVersionFromBuildProperties `
        -Content $properties `
        -Source 'test properties') `
    'Build properties parsing must return the single product version.'
Assert-ThrowsLike `
    -Action {
        Get-OpenBladeVersionFromBuildProperties `
            -Content '<Project />' `
            -Source 'missing properties'
    } `
    -Pattern '*exactly one OpenBladeVersion*found 0*' `
    -Message 'Missing product versions must fail closed.'
Assert-ThrowsLike `
    -Action {
        Get-OpenBladeVersionFromBuildProperties `
            -Content ($properties.Replace(
                    '</PropertyGroup>',
                    '<OpenBladeVersion>1.4.3</OpenBladeVersion></PropertyGroup>')) `
            -Source 'duplicate properties'
    } `
    -Pattern '*exactly one OpenBladeVersion*found 2*' `
    -Message 'Duplicate product versions must fail closed.'
Assert-ThrowsLike `
    -Action {
        Get-OpenBladeVersionFromBuildProperties `
            -Content ($properties.Replace('1.4.2', '1.04.2')) `
            -Source 'invalid properties'
    } `
    -Pattern '*without leading zeroes*' `
    -Message 'Non-canonical SemVer must fail closed.'

foreach ($case in @(
        @{
            Impact = 'major'
            Base = '1.4.2'
            Head = '2.0.0'
            Change = '1.4.2 -> 2.0.0'
        },
        @{
            Impact = 'minor'
            Base = '1.4.2'
            Head = '1.5.0'
            Change = '1.4.2 -> 1.5.0'
        },
        @{
            Impact = 'patch'
            Base = '1.4.2'
            Head = '1.4.3'
            Change = '1.4.2 -> 1.4.3'
        },
        @{
            Impact = 'none'
            Base = '1.4.2'
            Head = '1.4.2'
            Change = 'unchanged'
        },
        @{
            Impact = 'minor'
            Base = '0.1.0'
            Head = '0.2.0'
            Change = '0.1.0 -> 0.2.0'
        })) {
    $result = Assert-OpenBladePullRequestVersion `
        -BaseVersion $case.Base `
        -HeadVersion $case.Head `
        -Body (New-VersionBody `
            -Impact $case.Impact `
            -Change $case.Change)
    Assert-Equal $case.Impact $result.Impact `
        "The $($case.Impact) version policy must pass."
}

Assert-ThrowsLike `
    -Action {
        Assert-OpenBladePullRequestVersion `
            -BaseVersion '1.4.2' `
            -HeadVersion '1.4.2' `
            -Body 'No version fields.'
    } `
    -Pattern "*exactly one 'Version impact:' field*found 0*" `
    -Message 'Missing fields must fail closed.'
Assert-ThrowsLike `
    -Action {
        $body = (New-VersionBody -Impact 'none' -Change 'unchanged') +
            "`nVersion impact: patch"
        Assert-OpenBladePullRequestVersion `
            -BaseVersion '1.4.2' `
            -HeadVersion '1.4.2' `
            -Body $body
    } `
    -Pattern "*exactly one 'Version impact:' field*found 2*" `
    -Message 'Duplicate fields must fail closed.'
Assert-ThrowsLike `
    -Action {
        Assert-OpenBladePullRequestVersion `
            -BaseVersion '1.4.2' `
            -HeadVersion '1.4.2' `
            -Body (New-VersionBody `
                -Impact '<!-- major | minor | patch | none -->' `
                -Change '<!-- X.Y.Z -> X.Y.Z | unchanged -->' `
                -Reason '<!-- Explain the decision. -->')
    } `
    -Pattern "*'Version impact' field must replace its placeholder*" `
    -Message 'Template placeholders must fail closed.'
Assert-ThrowsLike `
    -Action {
        Assert-OpenBladePullRequestVersion `
            -BaseVersion '1.4.2' `
            -HeadVersion '1.4.4' `
            -Body (New-VersionBody `
                -Impact 'patch' `
                -Change '1.4.2 -> 1.4.4')
    } `
    -Pattern "*requires OpenBladeVersion '1.4.3'*contains '1.4.4'*" `
    -Message 'Skipped patch versions must fail closed.'
Assert-ThrowsLike `
    -Action {
        Assert-OpenBladePullRequestVersion `
            -BaseVersion '1.5.0' `
            -HeadVersion '1.5.0' `
            -Body (New-VersionBody `
                -Impact 'minor' `
                -Change '1.4.2 -> 1.5.0')
    } `
    -Pattern "*requires OpenBladeVersion '1.6.0'*contains '1.5.0'*" `
    -Message 'A stale version bump must fail against the current base.'
Assert-ThrowsLike `
    -Action {
        Assert-OpenBladePullRequestVersion `
            -BaseVersion '1.4.2' `
            -HeadVersion '1.4.3' `
            -Body (New-VersionBody `
                -Impact 'patch' `
                -Change '1.4.2 -> 1.4.9')
    } `
    -Pattern "*Version change must be '1.4.2 -> 1.4.3'*" `
    -Message 'The body must state the exact version change.'

$versionWorkflow = Get-Content -LiteralPath (
    Join-Path $repository '.github\workflows\pr-version.yml') -Raw
foreach ($requiredContract in @(
        '- opened',
        '- synchronize',
        '- reopened',
        '- edited',
        '- ready_for_review',
        'OPENBLADE_BASE_SHA:',
        'OPENBLADE_HEAD_SHA:',
        'OPENBLADE_PR_BODY:',
        './build/PullRequestVersion.ps1',
        'Get-OpenBladeVersionFromBuildProperties',
        'Assert-OpenBladePullRequestVersion')) {
    if (-not $versionWorkflow.Contains($requiredContract)) {
        throw "PR version workflow is missing '$requiredContract'."
    }
}
if ($versionWorkflow.Contains('build-and-test')) {
    throw 'PR description edits must not invoke the full build workflow.'
}

$ci = Get-Content -LiteralPath (
    Join-Path $repository '.github\workflows\ci.yml') -Raw
if ($ci.Contains('- edited')) {
    throw 'PR description edits must not rerun the full CI workflow.'
}

$template = Get-Content -LiteralPath (
    Join-Path $repository '.github\PULL_REQUEST_TEMPLATE.md') -Raw
foreach ($requiredField in @(
        'Version impact:',
        'Version change:',
        'Reason:')) {
    if (-not $template.Contains($requiredField)) {
        throw "The pull request template is missing '$requiredField'."
    }
}

Write-Host 'Pull request version tests passed.'
