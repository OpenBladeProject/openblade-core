Set-StrictMode -Version 3.0

function ConvertTo-OpenBladeCiPath {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $normalizedPath = $Path.Replace('\', '/')
    while ($normalizedPath.StartsWith('./', [StringComparison]::Ordinal)) {
        $normalizedPath = $normalizedPath.Substring(2)
    }

    return $normalizedPath
}

function Test-OpenBladeCiDocumentationPath {
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][string]$Path)

    return (
        $Path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -or
        $Path.Equals('LICENSE', [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith('docs/', [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith(
            '.github/ISSUE_TEMPLATE/',
            [StringComparison]::OrdinalIgnoreCase) -or
        $Path.Equals(
            '.github/PULL_REQUEST_TEMPLATE.md',
            [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith(
            '.github/PULL_REQUEST_TEMPLATE/',
            [StringComparison]::OrdinalIgnoreCase))
}

function Test-OpenBladeCiBuildRequired {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ChangedPath,

        [switch]$Force
    )

    if ($Force -or $ChangedPath.Count -eq 0) {
        return $true
    }

    foreach ($path in $ChangedPath) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            return $true
        }

        $normalizedPath = ConvertTo-OpenBladeCiPath -Path $path
        if (-not (Test-OpenBladeCiDocumentationPath -Path $normalizedPath)) {
            return $true
        }
    }

    return $false
}

function Test-OpenBladeCiInstallerRequired {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ChangedPath,

        [switch]$Force
    )

    if ($Force -or $ChangedPath.Count -eq 0) {
        return $true
    }

    foreach ($path in $ChangedPath) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            return $true
        }

        $normalizedPath = ConvertTo-OpenBladeCiPath -Path $path
        $isTestOnly = $normalizedPath.StartsWith(
            'tests/',
            [StringComparison]::OrdinalIgnoreCase)

        if (-not (Test-OpenBladeCiDocumentationPath -Path $normalizedPath) -and
            -not $isTestOnly) {
            return $true
        }
    }

    return $false
}
