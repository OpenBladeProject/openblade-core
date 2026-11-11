# Shared read-only assertions used by the actual evaluator and offline regressions.
function Assert-ExternalFixtureEvidenceRoot([string]$Repository, [string]$EvidenceRoot) {
    $source = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Repository))
    $destination = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($EvidenceRoot))
    if ($destination.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use an external private evaluation directory.'
    }
}

function Get-AgreedFixtureGraphRows([object[]]$Rows) {
    foreach ($group in $Rows | Group-Object layout, variant, project) {
        foreach ($field in @('target','intermediate','extensions','assets','identity','output','defines','references')) {
            if (@($group.Group.$field | Sort-Object -Unique -CaseSensitive).Count -ne 1) {
                throw "Root context disagreement: $field in $($group.Name)"
            }
        }
        $group.Group[0]
    }
}

function Assert-FixtureGraphIsolation([object[]]$Rows) {
    foreach ($group in $Rows | Group-Object layout, project) {
        $normal = $group.Group | Where-Object variant -eq normal
        if ($normal) {
            foreach ($fixture in $group.Group | Where-Object variant -ne normal) {
                foreach ($field in @('target','intermediate','extensions','assets','output','identity')) {
                    if ($fixture.$field -eq $normal.$field) { throw "Shared $field in $($group.Name): $($fixture.variant)" }
                }
            }
        }
        # Fixture-only conditional projects have no normal row. Their variants
        # still need independent target, restore, intermediate and output identity.
        foreach ($field in @('target','intermediate','extensions','assets','output','identity')) {
            if (@($group.Group.$field | Sort-Object -Unique).Count -ne $group.Count) { throw "Variant collision: $field in $($group.Name)" }
        }
    }
    foreach ($layout in @('ordinary','artifacts')) {
        foreach ($name in @('OpenBlade.Setup','OpenBlade.DeploymentBroker','OpenBlade.Deployment','OpenBlade.Contracts','OpenBlade.Deployment.Tests','OpenBlade.Setup.Tests','OpenBlade.Service','OpenBlade.Session','OpenBlade.DeploymentClient','OpenBlade.CandidateReceipt.Scratch')) {
            if (-not ($Rows | Where-Object { $_.layout -eq $layout -and $_.project -eq $name -and $_.variant -eq 'full' })) { throw "Missing reachable $name ($layout)" }
        }
    }
}
