Set-StrictMode -Version 3.0

function ConvertTo-OpenBladePullRequestVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Source
    )

    $normalized = $Version.Trim()
    $match = [Regex]::Match(
        $normalized,
        '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$')
    if (-not $match.Success) {
        throw "$Source must contain exactly three numeric SemVer components without leading zeroes: '$Version'."
    }

    $components = [ordered]@{}
    foreach ($name in @('major', 'minor', 'patch')) {
        $value = 0
        if (-not [int]::TryParse($match.Groups[$name].Value, [ref]$value) -or
            $value -gt 65535) {
            throw "$Source component '$name' must be between 0 and 65535: '$Version'."
        }
        $components[$name] = $value
    }

    return [pscustomobject]@{
        Text = $normalized
        Major = $components.major
        Minor = $components.minor
        Patch = $components.patch
    }
}

function Get-OpenBladeVersionFromBuildProperties {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Content,

        [Parameter(Mandatory)][string]$Source
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        throw "$Source is empty."
    }

    try {
        [xml]$document = $Content
    }
    catch {
        throw "$Source is not valid XML: $($_.Exception.Message)"
    }

    $nodes = @($document.SelectNodes('/Project/PropertyGroup/OpenBladeVersion'))
    if ($nodes.Count -ne 1) {
        throw "$Source must define exactly one OpenBladeVersion; found $($nodes.Count)."
    }

    $version = ConvertTo-OpenBladePullRequestVersion `
        -Version $nodes[0].InnerText `
        -Source "$Source OpenBladeVersion"
    return $version.Text
}

function Get-OpenBladePullRequestField {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Body,

        [Parameter(Mandatory)][string]$Name
    )

    $matches = [Regex]::Matches(
        $Body,
        "(?im)^[ \t]*$([Regex]::Escape($Name))[ \t]*:[ \t]*(?<value>[^\r\n]*?)[ \t]*\r?$")
    if ($matches.Count -ne 1) {
        throw "The pull request body must contain exactly one '${Name}:' field; found $($matches.Count)."
    }

    $value = $matches[0].Groups['value'].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($value) -or
        $value.IndexOf('<!--', [StringComparison]::Ordinal) -ge 0 -or
        $value.IndexOf('-->', [StringComparison]::Ordinal) -ge 0) {
        throw "The pull request '$Name' field must replace its placeholder with a value."
    }

    return $value
}

function Assert-OpenBladePullRequestVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseVersion,
        [Parameter(Mandatory)][string]$HeadVersion,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Body
    )

    $base = ConvertTo-OpenBladePullRequestVersion `
        -Version $BaseVersion `
        -Source 'The base OpenBladeVersion'
    $head = ConvertTo-OpenBladePullRequestVersion `
        -Version $HeadVersion `
        -Source 'The head OpenBladeVersion'
    $impact = (Get-OpenBladePullRequestField `
            -Body $Body `
            -Name 'Version impact').ToLowerInvariant()
    $declaredChange = Get-OpenBladePullRequestField `
        -Body $Body `
        -Name 'Version change'
    $reason = Get-OpenBladePullRequestField `
        -Body $Body `
        -Name 'Reason'

    $expectedVersion = switch ($impact) {
        'major' {
            if ($base.Major -eq 65535) {
                throw 'A major version bump would exceed the supported component range.'
            }
            "$($base.Major + 1).0.0"
        }
        'minor' {
            if ($base.Minor -eq 65535) {
                throw 'A minor version bump would exceed the supported component range.'
            }
            "$($base.Major).$($base.Minor + 1).0"
        }
        'patch' {
            if ($base.Patch -eq 65535) {
                throw 'A patch version bump would exceed the supported component range.'
            }
            "$($base.Major).$($base.Minor).$($base.Patch + 1)"
        }
        'none' {
            $base.Text
        }
        default {
            throw "Version impact must be major, minor, patch, or none; received '$impact'."
        }
    }

    if (-not $head.Text.Equals($expectedVersion, [StringComparison]::Ordinal)) {
        throw "Version impact '$impact' requires OpenBladeVersion '$expectedVersion' from base '$($base.Text)', but the PR contains '$($head.Text)'."
    }

    $expectedChange = if ($impact -eq 'none') {
        'unchanged'
    }
    else {
        "$($base.Text) -> $($head.Text)"
    }
    if (-not $declaredChange.Equals($expectedChange, [StringComparison]::Ordinal)) {
        throw "Version change must be '$expectedChange' for impact '$impact'; received '$declaredChange'."
    }

    return [pscustomobject]@{
        Impact = $impact
        BaseVersion = $base.Text
        HeadVersion = $head.Text
        VersionChange = $declaredChange
        Reason = $reason
    }
}
