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

function ConvertFrom-JsonString {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Source
    )

    if ($Value -notmatch '^"(?:[^"\\]|\\["\\/bfnrt]|\\u[0-9a-fA-F]{4})*"$') {
        throw "$Source must be a double-quoted string."
    }
    return ConvertFrom-Json -InputObject $Value
}

# These project manifests intentionally use a strict string-only TOML subset.
# Parse the complete file so duplicate keys, unknown keys, trailing syntax, and
# unterminated multiline instructions fail before semantic assertions run.
function ConvertFrom-OpenBladeAgentToml {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Source
    )

    $lines = @($Content -split '\r?\n')
    $values = [ordered]@{}
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($index -eq ($lines.Count - 1) -and $line.Length -eq 0) {
            continue
        }

        $assignment = [Regex]::Match(
            $line,
            '^(?<key>[a-z][a-z0-9_]*) = (?<value>.+)$')
        if (-not $assignment.Success) {
            throw "$Source has invalid TOML syntax at line $($index + 1)."
        }

        $key = $assignment.Groups['key'].Value
        if ($values.Contains($key)) {
            throw "$Source defines '$key' more than once."
        }

        $rawValue = $assignment.Groups['value'].Value
        if ($rawValue -eq '"""') {
            $body = @()
            $closed = $false
            for ($index++; $index -lt $lines.Count; $index++) {
                if ($lines[$index] -eq '"""') {
                    $closed = $true
                    break
                }
                $body += $lines[$index]
            }
            if (-not $closed) {
                throw "$Source has an unterminated multiline string for '$key'."
            }
            $values[$key] = [string]::Join("`n", [string[]]$body)
            continue
        }

        $values[$key] = ConvertFrom-JsonString `
            -Value $rawValue `
            -Source "$Source '$key'"
    }

    $requiredKeys = @(
        'name',
        'description',
        'model',
        'model_reasoning_effort',
        'sandbox_mode',
        'developer_instructions')
    foreach ($key in $requiredKeys) {
        if (-not $values.Contains($key)) {
            throw "$Source is missing required key '$key'."
        }
    }
    foreach ($key in $values.Keys) {
        if ($key -notin $requiredKeys) {
            throw "$Source contains unsupported key '$key'."
        }
    }

    return [pscustomobject]$values
}

function ConvertFrom-OpenBladeSkillMetadata {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Source
    )

    $lines = @($Content -split '\r?\n')
    if ($lines.Count -lt 2 -or $lines[0] -cne 'interface:') {
        throw "$Source must begin with the interface mapping."
    }

    $values = [ordered]@{}
    for ($index = 1; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($index -eq ($lines.Count - 1) -and $line.Length -eq 0) {
            continue
        }

        $entry = [Regex]::Match(
            $line,
            '^  (?<key>[a-z][a-z0-9_]*): (?<value>.+)$')
        if (-not $entry.Success) {
            throw "$Source has invalid YAML syntax at line $($index + 1)."
        }

        $key = $entry.Groups['key'].Value
        if ($values.Contains($key)) {
            throw "$Source defines interface.$key more than once."
        }
        $values[$key] = ConvertFrom-JsonString `
            -Value $entry.Groups['value'].Value `
            -Source "$Source interface.$key"
    }

    $requiredKeys = @('display_name', 'short_description', 'default_prompt')
    foreach ($key in $requiredKeys) {
        if (-not $values.Contains($key)) {
            throw "$Source is missing interface.$key."
        }
    }
    foreach ($key in $values.Keys) {
        if ($key -notin $requiredKeys) {
            throw "$Source contains unsupported interface key '$key'."
        }
    }

    return [pscustomobject]$values
}

function Assert-ContainsAll {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string[]]$RequiredValues,
        [Parameter(Mandatory)][string]$Source
    )

    foreach ($value in $RequiredValues) {
        if ($Content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "$Source is missing required safety contract '$value'."
        }
    }
}

function Test-ContainsUndefinedArtifactAuditMode {
    param([Parameter(Mandatory)][string]$Content)

    return [Regex]::IsMatch(
        $Content,
        'artifact[- ]audit',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

foreach ($legacyModeSpelling in @('artifact-audit', 'artifact audit')) {
    if (-not (Test-ContainsUndefinedArtifactAuditMode `
            -Content "stable-publish, nightly-publish, or $legacyModeSpelling")) {
        throw "The undefined-mode guard missed '$legacyModeSpelling'."
    }
}

$expectedAgents = @(
    [pscustomobject]@{
        File = 'openblade-release-operator.toml'
        Name = 'openblade_release_operator'
        Sandbox = 'workspace-write'
        RequiredInstructions = @(
            'full immutable SHA',
            'stable-publish or nightly-publish',
            'build/Publish.ps1',
            'Do not edit source, change the version, patch a failure, pass signing credentials',
            'install, request UAC, launch product processes, touch hardware',
            'push, tag, merge, publish a GitHub release',
            'delete a worktree')
    },
    [pscustomobject]@{
        File = 'openblade-validation-operator.toml'
        Name = 'openblade_validation_operator'
        Sandbox = 'workspace-write'
        RequiredInstructions = @(
            'absolute dedicated-worktree path',
            'build/Run-AffectedCi.ps1',
            'Do not edit source, publish production artifacts, install, request UAC, access live hardware',
            'mutate GitHub, delete anything')
    },
    [pscustomobject]@{
        File = 'openblade-offline-auditor.toml'
        Name = 'openblade_offline_auditor'
        Sandbox = 'read-only'
        RequiredInstructions = @(
            'Do not write files, run builds or tests, inspect unsanitized captures, open HID',
            'mutate GitHub, delete anything')
    })

foreach ($agent in $expectedAgents) {
    $path = Join-Path $agentDirectory $agent.File
    $manifest = ConvertFrom-OpenBladeAgentToml `
        -Content (Read-RequiredFile $path) `
        -Source $agent.File
    if ($manifest.name -cne $agent.Name) {
        throw "$($agent.File) must expose custom-agent name '$($agent.Name)'."
    }
    if ($manifest.model -cne 'gpt-5.3-codex-spark') {
        throw "$($agent.File) must remain pinned to GPT-5.3-Codex-Spark."
    }
    if ($manifest.model_reasoning_effort -cne 'medium') {
        throw "$($agent.File) must use the bounded medium reasoning profile."
    }
    if ($manifest.sandbox_mode -cne $agent.Sandbox) {
        throw "$($agent.File) has the wrong sandbox boundary."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.developer_instructions)) {
        throw "$($agent.File) must contain non-empty developer instructions."
    }
    Assert-ContainsAll `
        -Content $manifest.developer_instructions `
        -RequiredValues $agent.RequiredInstructions `
        -Source $agent.File
}

$releaseContent = Read-RequiredFile (
    Join-Path $agentDirectory 'openblade-release-operator.toml')
$releaseManifest = ConvertFrom-OpenBladeAgentToml `
    -Content $releaseContent `
    -Source 'openblade-release-operator.toml'
if (Test-ContainsUndefinedArtifactAuditMode `
        -Content $releaseManifest.developer_instructions) {
    throw 'The release operator still exposes the undefined artifact-audit mode.'
}

$duplicateKeyRejected = $false
try {
    [void](ConvertFrom-OpenBladeAgentToml `
            -Content ($releaseContent.TrimEnd() + "`nname = `"duplicate`"`n") `
            -Source 'duplicate-key-test.toml')
}
catch {
    $duplicateKeyRejected = $true
}
if (-not $duplicateKeyRejected) {
    throw 'The agent TOML parser accepted a duplicate key.'
}

$skill = Read-RequiredFile (Join-Path $skillDirectory 'SKILL.md')
Assert-Matches $skill '(?m)^name: openblade-spark-operators\r?$' `
    'The dispatcher skill has the wrong frontmatter name.'
Assert-Matches $skill `
    '(?m)^description: [^\r\n]*when the runtime exposes exact named-agent selection[^\r\n]*\r?$' `
    'The dispatcher description must disclose its named-agent dependency.'
foreach ($agent in $expectedAgents) {
    if ($skill.IndexOf($agent.Name, [StringComparison]::Ordinal) -lt 0) {
        throw "The dispatcher skill does not route to '$($agent.Name)'."
    }
}
if ($skill.IndexOf('[TODO', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The dispatcher skill contains unfinished scaffold text.'
}

$capabilityGateHeading = '## Confirm named-agent availability'
$routingHeading = '## Route the work'
$capabilityGateIndex = $skill.IndexOf(
    $capabilityGateHeading,
    [StringComparison]::Ordinal)
$routingIndex = $skill.IndexOf($routingHeading, [StringComparison]::Ordinal)
if ($capabilityGateIndex -lt 0 -or $routingIndex -lt 0 -or
        $capabilityGateIndex -ge $routingIndex) {
    throw 'The dispatcher must check named-agent capability before routing work.'
}
$capabilityGateStart = $capabilityGateIndex + $capabilityGateHeading.Length
$capabilityGate = $skill.Substring(
    $capabilityGateStart,
    $routingIndex - $capabilityGateStart)
Assert-ContainsAll `
    -Content $capabilityGate `
    -RequiredValues @(
        'exact custom-agent selector',
        'does not prove that this client can invoke those profiles',
        'task name, thread label, prompt, or generic model override',
        'Do not pass an unsupported model name',
        'Ask for explicit user approval',
        'Only describe a delegated run as Spark') `
    -Source 'SKILL.md named-agent capability gate'

Assert-ContainsAll `
    -Content $skill `
    -RequiredValues @(
        'Do not delegate any of the following',
        'source-code fixes',
        'exact-device admission',
        'live `OpenBlade.Capture`',
        'signing, installation, UAC',
        'pushing, tagging, merging',
        'destructive worktree') `
    -Source 'SKILL.md'

foreach ($reference in @(
        'release-operator.md',
        'validation-operator.md',
        'offline-auditor.md')) {
    [void](Read-RequiredFile (Join-Path $skillDirectory "references\$reference"))
    if ($skill.IndexOf("references/$reference", [StringComparison]::Ordinal) -lt 0) {
        throw "The dispatcher skill does not route to '$reference'."
    }
}

$releaseReference = Read-RequiredFile (
    Join-Path $skillDirectory 'references\release-operator.md')
Assert-ContainsAll `
    -Content $releaseReference `
    -RequiredValues @(
        'Mode: `stable-publish` or `nightly-publish`',
        'reject any other mode') `
    -Source 'references/release-operator.md'
foreach ($boundedSource in @(
        [pscustomobject]@{ Name = 'SKILL.md'; Content = $skill },
        [pscustomobject]@{
            Name = 'references/release-operator.md'
            Content = $releaseReference
        })) {
    if (Test-ContainsUndefinedArtifactAuditMode -Content $boundedSource.Content) {
        throw "$($boundedSource.Name) still exposes the undefined artifact-audit mode."
    }
}

$metadata = ConvertFrom-OpenBladeSkillMetadata `
    -Content (Read-RequiredFile (
        Join-Path $skillDirectory 'agents\openai.yaml')) `
    -Source 'agents/openai.yaml'
if (-not $metadata.default_prompt.StartsWith(
        'Use $openblade-spark-operators ',
        [StringComparison]::Ordinal)) {
    throw 'The skill UI metadata must invoke $openblade-spark-operators.'
}
if ($metadata.default_prompt.IndexOf(
        'verify exact named-agent support',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'The skill UI metadata must expose the named-agent preflight.'
}

Write-Host 'Spark agent and skill workflow contracts passed.'
