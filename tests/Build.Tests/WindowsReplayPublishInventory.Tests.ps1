[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

# Parse the publish entry points without executing Publish.ps1 or any helper.
$inventory = Get-Content -LiteralPath (Join-Path $repository 'docs/windows-backend-replay-inventory.json') -Raw |
    ConvertFrom-Json
$publishScript = Join-Path $repository 'build/Publish.ps1'
$tokens = $null
$parseErrors = $null
$publishAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $publishScript, [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'Unable to parse the Windows publish entry points.'
$actualProjects = @($publishAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Invoke-CheckedPublish'
}, $true) | ForEach-Object {
    $project = [regex]::Match($_.CommandElements[1].Extent.Text, 'src[/\\](?<name>OpenBlade\.[\w.]+)[/\\]\k<name>\.csproj')
    $destination = $_.CommandElements[2]
    Assert-True ($project.Success -and
        $destination -is [System.Management.Automation.Language.VariableExpressionAst]) 'Unrecognized Windows publish entry.'
    '{0} -> {1}' -f $project.Groups['name'].Value, $destination.VariablePath.UserPath
})
$expectedProjects = @($inventory.publishProjects | ForEach-Object { '{0} -> {1}' -f $_.project, $_.staging })
$projectComparison = @{
    ReferenceObject = $expectedProjects
    DifferenceObject = $actualProjects
    CaseSensitive = $true
}
$publishDifferences = @(Compare-Object @projectComparison)
Assert-True ($publishDifferences.Count -eq 0) (
    'Windows replay publish inventory differs from Publish.ps1: ' + ($publishDifferences | Out-String))
function Resolve-InventorySource([string]$Path) {
    Assert-True ($Path -cmatch '^src/OpenBlade\.[\w.]+/[\w.]+\.cs$') "Unexpected inventory source path: $Path"
    $absolute = Join-Path $repository $Path
    Assert-True (Test-Path -LiteralPath $absolute -PathType Leaf) "Missing inventory source: $Path"
    return $absolute
}

foreach ($mode in $inventory.runtimeModes) {
    $body = Get-Content -LiteralPath (Resolve-InventorySource $mode.source) -Raw
    $literal = '"' + $mode.mode + '"'
    Assert-True ($body.Contains($literal)) "Missing runtime mode $($mode.mode) in $($mode.source)."
}

foreach ($entry in $inventory.persistence) {
    Assert-True ($entry.readers.Count -eq $entry.readerSources.Count) 'Persistence reader/source counts differ.'
    for ($index = 0; $index -lt $entry.readers.Count; $index++) {
        $body = Get-Content -LiteralPath (Resolve-InventorySource $entry.readerSources[$index]) -Raw
        $reader = [regex]::Escape($entry.readers[$index])
        Assert-True ($body -cmatch "\b(class|record)\s+$reader\b") "Missing persistence reader $($entry.readers[$index])."
    }
}
Write-Host 'Windows replay publish inventory matches the package entry points.'
