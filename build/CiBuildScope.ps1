Set-StrictMode -Version 3.0

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

        $normalizedPath = $path.Replace('\', '/')
        while ($normalizedPath.StartsWith('./', [StringComparison]::Ordinal)) {
            $normalizedPath = $normalizedPath.Substring(2)
        }

        $isDocumentationOnly =
            $normalizedPath.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.Equals('LICENSE', [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith('docs/', [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith(
                '.github/ISSUE_TEMPLATE/',
                [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.Equals(
                '.github/PULL_REQUEST_TEMPLATE.md',
                [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith(
                '.github/PULL_REQUEST_TEMPLATE/',
                [StringComparison]::OrdinalIgnoreCase)

        if (-not $isDocumentationOnly) {
            return $true
        }
    }

    return $false
}
