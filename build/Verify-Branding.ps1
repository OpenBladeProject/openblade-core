[CmdletBinding()]
param(
    [string]$Repository
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = Split-Path -Parent $PSScriptRoot
}

$repositoryRoot = [IO.Path]::GetFullPath($Repository)
$formerToken = 'Open' + 'Synapse'
$violations = [Collections.Generic.List[string]]::new()
$tracked = & git -C $repositoryRoot ls-files --cached
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed with exit code $LASTEXITCODE"
}

foreach ($relativePath in $tracked) {
    if ($relativePath.IndexOf($formerToken, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $violations.Add("tracked path: $relativePath")
    }

    $absolutePath = Join-Path $repositoryRoot $relativePath
    if (-not [IO.File]::Exists($absolutePath)) {
        # A staged or working-tree deletion is not part of the bundle being
        # verified.
        continue
    }
    $lines = [IO.File]::ReadAllLines($absolutePath)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        if ($line.IndexOf($formerToken, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }

        $normalizedPath = $relativePath.Replace('\', '/')
        $isRenameRecord = $normalizedPath -in @(
            'docs/superpowers/specs/2026-07-14-openblade-clean-break-rename-design.md',
            'docs/superpowers/plans/2026-07-14-openblade-clean-break-rename.md'
        )
        if ($isRenameRecord) {
            continue
        }

        $isCaptureEvidence =
            $normalizedPath.StartsWith('captures/annotations/', [StringComparison]::Ordinal) -or
            $normalizedPath.StartsWith('captures/decoded/', [StringComparison]::Ordinal)
        $isHistoricalPcap = $line -cmatch ('^\s*"(?:fileName|rawCapture|captureFileName)"\s*:\s*"open' + 'synapse-[^"]+\.pcap"\s*,?\s*$')
        if ($isCaptureEvidence -and $isHistoricalPcap) {
            continue
        }

        $violations.Add("${relativePath}:$($index + 1):$line")
    }
}

if ($violations.Count -ne 0) {
    $violations | ForEach-Object { [Console]::Error.WriteLine($_) }
    exit 1
}

Write-Host 'OpenBlade branding verification passed.'
