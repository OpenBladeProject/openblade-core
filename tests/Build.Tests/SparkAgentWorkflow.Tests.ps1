[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$agentDirectory = Join-Path $repository '.codex\agents'
$skillDirectory = Join-Path $repository (
    '.agents\skills\openblade-spark-operators')

function Read-RequiredFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required Spark workflow file is missing: $Path"
    }
    return [IO.File]::ReadAllText($Path)
}

function Assert-Matches {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not [Regex]::IsMatch($Content, $Pattern)) {
        throw $Message
    }
}

$expectedAgents = @(
    [pscustomobject]@{
        File = 'openblade-release-operator.toml'
        Name = 'openblade_release_operator'
        Sandbox = 'workspace-write'
        RequiredInstruction = 'build/Publish\.ps1'
    },
    [pscustomobject]@{
        File = 'openblade-validation-operator.toml'
        Name = 'openblade_validation_operator'
        Sandbox = 'workspace-write'
        RequiredInstruction = 'build/Run-AffectedCi\.ps1'
    },
    [pscustomobject]@{
        File = 'openblade-offline-auditor.toml'
        Name = 'openblade_offline_auditor'
        Sandbox = 'read-only'
        RequiredInstruction = 'Do not write files'
    })

foreach ($agent in $expectedAgents) {
    $content = Read-RequiredFile (Join-Path $agentDirectory $agent.File)
    Assert-Matches $content "(?m)^name = `"$([Regex]::Escape($agent.Name))`"\r?$" `
        "$($agent.File) must expose its configured custom-agent name."
    Assert-Matches $content '(?m)^model = "gpt-5\.3-codex-spark"\r?$' `
        "$($agent.File) must remain pinned to GPT-5.3-Codex-Spark."
    Assert-Matches $content '(?m)^model_reasoning_effort = "medium"\r?$' `
        "$($agent.File) must use the bounded medium reasoning profile."
    Assert-Matches $content `
        "(?m)^sandbox_mode = `"$([Regex]::Escape($agent.Sandbox))`"\r?$" `
        "$($agent.File) has the wrong sandbox boundary."
    Assert-Matches $content '(?s)developer_instructions = """\s*\S.+\s*"""\s*$' `
        "$($agent.File) must contain non-empty developer instructions."
    Assert-Matches $content $agent.RequiredInstruction `
        "$($agent.File) is missing its defining workflow boundary."
}

$skill = Read-RequiredFile (Join-Path $skillDirectory 'SKILL.md')
Assert-Matches $skill '(?m)^name: openblade-spark-operators\r?$' `
    'The dispatcher skill has the wrong frontmatter name.'
foreach ($agent in $expectedAgents) {
    if ($skill.IndexOf($agent.Name, [StringComparison]::Ordinal) -lt 0) {
        throw "The dispatcher skill does not route to '$($agent.Name)'."
    }
}
if ($skill.IndexOf('[TODO', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The dispatcher skill contains unfinished scaffold text.'
}

foreach ($reference in @(
        'release-operator.md',
        'validation-operator.md',
        'offline-auditor.md')) {
    [void](Read-RequiredFile (Join-Path $skillDirectory "references\$reference"))
    if ($skill.IndexOf("references/$reference", [StringComparison]::Ordinal) -lt 0) {
        throw "The dispatcher skill does not route to '$reference'."
    }
}

$metadata = Read-RequiredFile (Join-Path $skillDirectory 'agents\openai.yaml')
Assert-Matches $metadata `
    '(?m)^  default_prompt: "Use \$openblade-spark-operators .+"\r?$' `
    'The skill UI metadata must provide a default prompt that invokes the skill.'

Write-Host 'Spark agent and skill workflow contracts passed.'
