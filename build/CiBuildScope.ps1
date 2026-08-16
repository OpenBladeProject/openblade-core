Set-StrictMode -Version 3.0

$script:OpenBladeCiRepository = Split-Path -Parent $PSScriptRoot

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

function Test-OpenBladeCiPattern {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern
    )

    return $Path -like $Pattern
}

function Get-OpenBladeCiManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repository)

    $manifestPath = Join-Path $Repository 'build\CiImpactManifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The CI impact manifest is missing: $manifestPath"
    }

    return Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}

function Get-OpenBladeCiProjectCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repository)

    $repositoryPath = [IO.Path]::GetFullPath($Repository).TrimEnd('\', '/')
    $projectFiles = @()
    foreach ($relativeRoot in @('src', 'tests', 'build\installer')) {
        $root = Join-Path $repositoryPath $relativeRoot
        if (Test-Path -LiteralPath $root -PathType Container) {
            $projectFiles += Get-ChildItem -LiteralPath $root -Filter '*.csproj' -Recurse
        }
    }

    $nodes = @{}
    foreach ($projectFile in $projectFiles) {
        $relativePath = $projectFile.FullName.Substring($repositoryPath.Length).TrimStart('\', '/')
        $relativePath = ConvertTo-OpenBladeCiPath -Path $relativePath
        [xml]$project = Get-Content -LiteralPath $projectFile.FullName
        $references = @()
        foreach ($reference in @($project.SelectNodes('//ProjectReference'))) {
            if ($null -eq $reference -or
                [string]::IsNullOrWhiteSpace([string]$reference.Include)) {
                continue
            }

            $referencePath = [IO.Path]::GetFullPath((Join-Path $projectFile.DirectoryName ([string]$reference.Include)))
            if (-not $referencePath.StartsWith(
                    $repositoryPath + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Project reference leaves the repository: $relativePath"
            }

            $referenceRelative = $referencePath.Substring($repositoryPath.Length).TrimStart('\', '/')
            $references += ConvertTo-OpenBladeCiPath -Path $referenceRelative
        }

        $isTest = $relativePath.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) -or
            $relativePath.IndexOf('.Tests/', [StringComparison]::OrdinalIgnoreCase) -ge 0
        $node = [pscustomobject]@{
            Path = $relativePath
            Directory = $relativePath.Substring(0, $relativePath.LastIndexOf('/'))
            Kind = if ($isTest) { 'Test' } else { 'Production' }
            References = @($references)
        }
        $nodes[$relativePath.ToLowerInvariant()] = $node
    }

    $trayTestsKey = 'tests/openblade.tray.tests/openblade.tray.tests.csproj'
    if ($nodes.ContainsKey($trayTestsKey)) {
        $nodes[$trayTestsKey].References += 'src/OpenBlade.Tray/OpenBlade.Tray.csproj'
    }

    return $nodes
}

function New-OpenBladeCiFullPlan {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]]$ChangedPath,
        [Parameter(Mandatory)][string]$Reason
    )

    return [pscustomobject]@{
        Mode = 'full'
        ChangedPaths = @($ChangedPath)
        AffectedProjects = @('OpenBlade.slnx')
        QualityProjects = @('OpenBlade.slnx')
        TestProjects = @('OpenBlade.slnx')
        ContractTests = @('*')
        ContractRestoreProjects = @('OpenBlade.slnx')
        PublishProjects = @()
        RequiresCaptures = $true
        RunInstaller = $true
        Reasons = @($Reason)
    }
}

function Get-OpenBladeCiPlan {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()]
        [string[]]$ChangedPath,

        [string]$Repository = $script:OpenBladeCiRepository,

        [switch]$ForceFull
    )

    $normalizedPaths = @($ChangedPath | ForEach-Object {
        if ($null -eq $_) { '' } else { ConvertTo-OpenBladeCiPath -Path $_ }
    })
    if ($ForceFull) {
        return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason 'The workflow boundary requires exhaustive validation.'
    }
    if ($normalizedPaths.Count -eq 0) {
        return New-OpenBladeCiFullPlan -ChangedPath @() -Reason 'The changed path set is empty or unavailable.'
    }
    foreach ($path in $normalizedPaths) {
        if ([string]::IsNullOrWhiteSpace($path) -or
            [IO.Path]::IsPathRooted($path) -or
            @($path.Split('/')) -contains '..') {
            return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "The changed path '$path' is invalid."
        }
    }

    try {
        $repositoryPath = [IO.Path]::GetFullPath($Repository)
        $manifest = Get-OpenBladeCiManifest -Repository $repositoryPath
        $projects = Get-OpenBladeCiProjectCatalog -Repository $repositoryPath
    }
    catch {
        return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "CI impact discovery failed closed: $($_.Exception.Message)"
    }

    foreach ($path in $normalizedPaths) {
        if ($path.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
            return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "Project metadata changed: $path"
        }
        foreach ($pattern in @($manifest.globalPatterns)) {
            if (Test-OpenBladeCiPattern -Path $path -Pattern ([string]$pattern)) {
                return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "Global CI input changed: $path"
            }
        }
    }

    $seedProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directTests = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $contractTests = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $contractRestoreProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $reasons = [Collections.Generic.List[string]]::new()
    $requiresCaptures = $false
    $runInstaller = $false
    $recognizedNonDocumentation = $false

    foreach ($path in $normalizedPaths) {
        $pathRecognized = $false

        foreach ($rule in @($manifest.contractRules)) {
            $matches = $false
            foreach ($pattern in @($rule.patterns)) {
                if (Test-OpenBladeCiPattern -Path $path -Pattern ([string]$pattern)) {
                    $matches = $true
                    break
                }
            }
            if (-not $matches) { continue }

            [void]$contractTests.Add([string]$rule.test)
            $restoreProjects = if ($rule.PSObject.Properties.Name -contains 'restoreProjects') {
                @($rule.restoreProjects)
            }
            else { @() }
            foreach ($restoreProject in $restoreProjects) {
                if (-not [string]::IsNullOrWhiteSpace([string]$restoreProject)) {
                    [void]$contractRestoreProjects.Add([string]$restoreProject)
                }
            }
            if ($rule.PSObject.Properties.Name -contains 'requiresCaptures' -and
                [bool]$rule.requiresCaptures) {
                $requiresCaptures = $true
            }
            $pathRecognized = $true
            $reasons.Add("$path selects $($rule.test).")
        }

        foreach ($rule in @($manifest.testInputRules)) {
            $matches = $false
            foreach ($pattern in @($rule.patterns)) {
                if (Test-OpenBladeCiPattern -Path $path -Pattern ([string]$pattern)) {
                    $matches = $true
                    break
                }
            }
            if (-not $matches) { continue }

            $ruleSeedProjects = if ($rule.PSObject.Properties.Name -contains 'seedProjects') {
                @($rule.seedProjects)
            }
            else { @() }
            foreach ($project in $ruleSeedProjects) {
                if (-not [string]::IsNullOrWhiteSpace([string]$project)) {
                    [void]$seedProjects.Add([string]$project)
                }
            }
            $ruleTestProjects = if ($rule.PSObject.Properties.Name -contains 'testProjects') {
                @($rule.testProjects)
            }
            else { @() }
            foreach ($project in $ruleTestProjects) {
                if (-not [string]::IsNullOrWhiteSpace([string]$project)) {
                    [void]$directTests.Add([string]$project)
                }
            }
            if ($rule.PSObject.Properties.Name -contains 'requiresCaptures' -and
                [bool]$rule.requiresCaptures) {
                $requiresCaptures = $true
            }
            $pathRecognized = $true
            $reasons.Add("${path}: $($rule.reason)")
        }

        foreach ($pattern in @($manifest.fullInstallerPatterns)) {
            if (Test-OpenBladeCiPattern -Path $path -Pattern ([string]$pattern)) {
                $runInstaller = $true
                $pathRecognized = $true
                $reasons.Add("$path affects the complete installer boundary.")
                break
            }
        }

        $projectOwner = $null
        foreach ($node in @($projects.Values | Sort-Object { $_.Directory.Length } -Descending)) {
            if ($path.StartsWith(
                    $node.Directory + '/',
                    [StringComparison]::OrdinalIgnoreCase)) {
                $projectOwner = $node
                break
            }
        }
        if ($null -ne $projectOwner) {
            [void]$seedProjects.Add($projectOwner.Path)
            $pathRecognized = $true
            $recognizedNonDocumentation = $true
            $reasons.Add("$path is owned by $($projectOwner.Path).")
        }

        if ($path.StartsWith('tests/Build.Tests/', [StringComparison]::OrdinalIgnoreCase)) {
            if ($path.EndsWith('.Tests.ps1', [StringComparison]::OrdinalIgnoreCase)) {
                [void]$contractTests.Add($path)
                $ownRule = @($manifest.contractRules | Where-Object {
                    ([string]$_.test).Equals($path, [StringComparison]::OrdinalIgnoreCase)
                } | Select-Object -First 1)
                $contractPath = Join-Path $repositoryPath ($path.Replace('/', [IO.Path]::DirectorySeparatorChar))
                if ($ownRule.Count -eq 0 -or
                    -not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
                    return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "Build-contract suite '$path' is not fully declared."
                }
                if ($ownRule[0].PSObject.Properties.Name -contains 'restoreProjects') {
                    foreach ($restoreProject in @($ownRule[0].restoreProjects)) {
                        [void]$contractRestoreProjects.Add([string]$restoreProject)
                    }
                }
                if ($ownRule[0].PSObject.Properties.Name -contains 'requiresCaptures' -and
                    [bool]$ownRule[0].requiresCaptures) {
                    $requiresCaptures = $true
                }
                $pathRecognized = $true
                $recognizedNonDocumentation = $true
                $reasons.Add("$path selects its own build-contract suite.")
            }
            else {
                return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "Unknown build-contract input changed: $path"
            }
        }

        if ($pathRecognized) {
            if (-not (Test-OpenBladeCiDocumentationPath -Path $path)) {
                $recognizedNonDocumentation = $true
            }
            continue
        }
        if (Test-OpenBladeCiDocumentationPath -Path $path) {
            continue
        }
        if ($path -in @('.gitignore')) {
            continue
        }

        return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "No CI impact rule owns '$path'."
    }

    $reverse = @{}
    foreach ($node in @($projects.Values)) {
        foreach ($reference in @($node.References)) {
            $key = ([string]$reference).ToLowerInvariant()
            if (-not $reverse.ContainsKey($key)) { $reverse[$key] = @() }
            $reverse[$key] = @($reverse[$key]) + $node.Path
        }
    }

    $affected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($seed in $seedProjects) {
        if (-not $projects.ContainsKey($seed.ToLowerInvariant())) {
            return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "Declared project '$seed' is missing."
        }
        if ($affected.Add($seed)) { $queue.Enqueue($seed) }
    }
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $key = $current.ToLowerInvariant()
        if (-not $reverse.ContainsKey($key)) { continue }
        foreach ($consumer in @($reverse[$key])) {
            if ($affected.Add([string]$consumer)) { $queue.Enqueue([string]$consumer) }
        }
    }
    foreach ($testProject in $directTests) {
        if (-not $projects.ContainsKey($testProject.ToLowerInvariant())) {
            return New-OpenBladeCiFullPlan -ChangedPath $normalizedPaths -Reason "Declared test project '$testProject' is missing."
        }
        [void]$affected.Add($testProject)
    }

    $impactProjects = @($affected)
    $quality = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $impactProjects) {
        [void]$quality.Add($project)
    }
    if ($quality.Contains('tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj')) {
        # Tray.Tests invokes the WinUI project through a custom MSBuild target,
        # so restore it explicitly even though it is not a ProjectReference.
        [void]$quality.Add('src/OpenBlade.Tray/OpenBlade.Tray.csproj')
    }

    $qualityProjects = @($quality | Sort-Object)
    $testProjects = @($qualityProjects | Where-Object {
        $projects[$_.ToLowerInvariant()].Kind -eq 'Test'
    })
    $affectedProductionProjects = @($impactProjects | Where-Object {
        $projects[$_.ToLowerInvariant()].Kind -eq 'Production'
    })

    if ($testProjects -contains 'tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj') {
        $requiresCaptures = $true
    }
    if ($affectedProductionProjects -contains 'src/OpenBlade.Setup/OpenBlade.Setup.csproj') {
        $runInstaller = $true
    }

    $publishProjects = @()
    if (-not $runInstaller) {
        $publishProjects = @($affectedProductionProjects | Where-Object {
            @($manifest.publishableProjects) -contains $_
        } | Sort-Object)
    }

    if ($qualityProjects.Count -eq 0 -and $contractTests.Count -eq 0 -and -not $runInstaller) {
        return [pscustomobject]@{
            Mode = 'none'
            ChangedPaths = @($normalizedPaths)
            AffectedProjects = @()
            QualityProjects = @()
            TestProjects = @()
            ContractTests = @()
            ContractRestoreProjects = @()
            PublishProjects = @()
            RequiresCaptures = $false
            RunInstaller = $false
            Reasons = if ($recognizedNonDocumentation) { @($reasons) } else { @('No executable validation consumes the changed documentation.') }
        }
    }

    return [pscustomobject]@{
        Mode = 'affected'
        ChangedPaths = @($normalizedPaths)
        AffectedProjects = @($impactProjects | Sort-Object)
        QualityProjects = @($qualityProjects)
        TestProjects = @($testProjects)
        ContractTests = @($contractTests | Sort-Object)
        ContractRestoreProjects = @($contractRestoreProjects | Sort-Object)
        PublishProjects = @($publishProjects)
        RequiresCaptures = $requiresCaptures
        RunInstaller = $runInstaller
        Reasons = @($reasons)
    }
}

function Format-OpenBladeCiPlan {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)]$Plan)

    $lines = [Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]@(
        "## CI impact: $($Plan.Mode)",
        '',
        "- Directly affected projects: $(@($Plan.AffectedProjects).Count)",
        "- Quality projects: $(@($Plan.QualityProjects).Count)",
        "- Test projects: $(@($Plan.TestProjects).Count)",
        "- Build-contract suites: $(@($Plan.ContractTests).Count)",
        "- Component publishes: $(@($Plan.PublishProjects).Count)",
        "- Full installer: $($Plan.RunInstaller)",
        "- Capture evidence required: $($Plan.RequiresCaptures)"
    ))
    foreach ($selection in @(
            [pscustomobject]@{ Name = 'Quality projects'; Values = @($Plan.QualityProjects) },
            [pscustomobject]@{ Name = 'Test projects'; Values = @($Plan.TestProjects) },
            [pscustomobject]@{ Name = 'Build-contract suites'; Values = @($Plan.ContractTests) },
            [pscustomobject]@{ Name = 'Component publishes'; Values = @($Plan.PublishProjects) })) {
        $lines.Add('')
        $lines.Add("### $($selection.Name)")
        if ($selection.Values.Count -eq 0) {
            $lines.Add('- None')
        }
        else {
            foreach ($value in $selection.Values) { $lines.Add("- ``$value``") }
        }
    }
    $lines.Add('')
    $lines.Add('### Reasons')
    foreach ($reason in @($Plan.Reasons)) { $lines.Add("- $reason") }
    return $lines -join [Environment]::NewLine
}

function Test-OpenBladeCiBuildRequired {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ChangedPath,
        [switch]$Force
    )

    return (Get-OpenBladeCiPlan -ChangedPath $ChangedPath -ForceFull:$Force).Mode -ne 'none'
}

function Test-OpenBladeCiInstallerRequired {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ChangedPath,
        [switch]$Force
    )

    return (Get-OpenBladeCiPlan -ChangedPath $ChangedPath -ForceFull:$Force).RunInstaller
}
