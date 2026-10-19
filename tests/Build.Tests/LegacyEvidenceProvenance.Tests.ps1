param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$provenancePath = Join-Path $repository 'evidence\provenance.json'
$evidenceRoot = Join-Path $repository 'evidence\legacy-captures'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

Assert-True (Test-Path -LiteralPath $provenancePath -PathType Leaf) `
    'The legacy evidence provenance map is missing.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $repository '.gitmodules'))) `
    'The obsolete captures submodule configuration must stay removed.'
$trackedCaptureRoot = @(& git -C $repository ls-files --stage -- captures)
Assert-True ($LASTEXITCODE -eq 0) 'Unable to inspect the tracked captures path.'
Assert-True ($trackedCaptureRoot.Count -eq 0) `
    'The obsolete captures gitlink must stay removed.'
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
Assert-True ($provenance.schemaVersion -eq 1) 'The provenance schema changed unexpectedly.'
Assert-True ($provenance.sourceRepository -ceq `
        'https://github.com/OpenBladeProject/openblade-captures') `
    'The original evidence repository attribution changed.'
Assert-True ($provenance.sourceRevision -ceq `
        '4f2bf1565c7557439760db7e45104bb1e8b62bfd') `
    'The imported evidence revision changed.'
Assert-True ($null -eq $provenance.sourceLicenseFile) `
    'The source revision did not contain a separate license file.'

$seenSourcePaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$seenPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($entry in @($provenance.files)) {
    $sourcePath = [string]$entry.sourcePath
    $path = [string]$entry.path
    Assert-True ($sourcePath -cmatch '^(annotations|decoded)/[^/]+\.json$') `
        "Unexpected source evidence path '$sourcePath'."
    Assert-True ($path -ceq "legacy-captures/$sourcePath") `
        "Destination '$path' does not preserve '$sourcePath'."
    Assert-True $seenSourcePaths.Add($sourcePath) "Duplicate source path '$sourcePath'."
    Assert-True $seenPaths.Add($path) "Duplicate destination path '$path'."

    $localPath = Join-Path (Join-Path $repository 'evidence') `
        $path.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Assert-True (Test-Path -LiteralPath $localPath -PathType Leaf) `
        "Imported evidence is missing: $path"
    $actualHash = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actualHash -ceq [string]$entry.sha256) `
        "Imported evidence hash changed: $path"
}

$actualPaths = @(Get-ChildItem -LiteralPath $evidenceRoot -Filter '*.json' -File -Recurse |
    ForEach-Object {
        $relative = $_.FullName.Substring((Split-Path -Parent $evidenceRoot).Length + 1)
        $relative.Replace('\', '/')
    } | Sort-Object)
$declaredPaths = @($provenance.files | ForEach-Object { [string]$_.path } | Sort-Object)
$difference = @(Compare-Object $declaredPaths $actualPaths)
Assert-True ($difference.Count -eq 0) `
    "The imported evidence set and provenance map differ: $($difference -join ', ')"

Write-Host "Legacy evidence provenance tests passed ($($declaredPaths.Count) files)."
