#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot)))
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if ($EvidenceRoot.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an external private evaluation directory.'
}
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
# Inject a read/evaluation-only target. No compiler, product entrypoint or probe.
$target = Join-Path $EvidenceRoot 'Snapshot.targets'
@'
<Project>
  <Target Name="SnapshotOpenBladeFixtureGraph">
    <PropertyGroup>
      <_SnapshotReferences>@(ProjectReference->'%(FullPath)', ',')</_SnapshotReferences>
      <_SnapshotDefines>$([System.String]::Copy('$(DefineConstants)').Replace(';', ','))</_SnapshotDefines>
    </PropertyGroup>
    <WriteLinesToFile File="$(FixtureEvaluationRoot)/$(MSBuildProjectName).txt" Overwrite="true"
      Lines="$(MSBuildProjectName)|$(TargetPath)|$(BaseIntermediateOutputPath)|$(MSBuildProjectExtensionsPath)|$(ProjectAssetsFile)|$(ArtifactsProjectName)|$(OutputPath)|$(_SnapshotDefines)|$(_SnapshotReferences)" />
    <MSBuild Projects="@(ProjectReference)" Targets="SnapshotOpenBladeFixtureGraph" BuildInParallel="false" />
  </Target>
</Project>
'@ | Set-Content -LiteralPath $target
$variants = [ordered]@{
    normal = @()
    native = @('OpenBladeNativePackageTests')
    candidate = @('OpenBladeCandidateReceiptFixture')
    proof = @('OpenBladePackageProofFixture')
    candidateProof = @('OpenBladeCandidateReceiptFixture', 'OpenBladePackageProofFixture')
    aot = @('OpenBladeCandidateReceiptFixture', 'OpenBladePackageProofFixture', 'OpenBladeScratchNativeReceiver')
    full = @('OpenBladeNativePackageTests', 'OpenBladeCandidateReceiptFixture', 'OpenBladePackageProofFixture')
}
$rows = @()
foreach ($layout in @('ordinary', 'artifacts')) {
    foreach ($variant in $variants.GetEnumerator()) {
        $destination = Join-Path $EvidenceRoot ($layout + '-' + $variant.Key)
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $arguments = @('msbuild', (Join-Path $repository 'tests/OpenBlade.Setup.Tests/OpenBlade.Setup.Tests.csproj'),
            '-nologo', '-t:SnapshotOpenBladeFixtureGraph', '-p:Configuration=Release',
            "-p:CustomAfterMicrosoftCommonTargets=$target", "-p:FixtureEvaluationRoot=$destination")
        if ($layout -eq 'artifacts') { $arguments += "-p:ArtifactsPath=$(Join-Path $EvidenceRoot 'shared-artifacts')" }
        foreach ($flag in $variant.Value) { $arguments += "-p:$flag=true" }
        # Setup.Tests covers installed managed consumers; the client and AOT
        # receiver are separate roots of the same globally flagged build graph.
        foreach ($root in @('tests/OpenBlade.Setup.Tests/OpenBlade.Setup.Tests.csproj',
            'src/OpenBlade.DeploymentClient/OpenBlade.DeploymentClient.csproj',
            'tests/OpenBlade.CandidateReceipt.Scratch/OpenBlade.CandidateReceipt.Scratch.csproj')) {
            $arguments[1] = Join-Path $repository $root
            $name = [IO.Path]::GetFileNameWithoutExtension($root)
            & dotnet @arguments *> (Join-Path $EvidenceRoot ($layout + '-' + $variant.Key + '-' + $name + '.log'))
            if ($LASTEXITCODE -ne 0) { throw "Evaluation failed: $layout $($variant.Key) $name" }
        }
        foreach ($file in Get-ChildItem -LiteralPath $destination -Filter '*.txt') {
            $cells = (Get-Content -LiteralPath $file.FullName -Raw).Trim() -split '\|'
            $rows += [pscustomobject]@{ layout=$layout;variant=$variant.Key;project=$cells[0];target=$cells[1];intermediate=$cells[2];extensions=$cells[3];assets=$cells[4];identity=$cells[5];output=$cells[6];defines=$cells[7];references=$cells[8] }
        }
    }
}
foreach ($group in $rows | Group-Object layout, project) {
    $normal = $group.Group | Where-Object variant -eq normal
    if (-not $normal) { continue }
    foreach ($fixture in $group.Group | Where-Object variant -ne normal) {
        foreach ($field in @('target','intermediate','extensions','assets','output','identity')) {
            if ($fixture.$field -eq $normal.$field) { throw "Shared $field in $($group.Name): $($fixture.variant)" }
        }
    }
    foreach ($field in @('target','intermediate','extensions','assets','output','identity')) {
        if (@($group.Group.$field | Sort-Object -Unique).Count -ne $group.Count) { throw "Variant collision: $field in $($group.Name)" }
    }
}
foreach ($layout in @('ordinary','artifacts')) {
    foreach ($name in @('OpenBlade.Setup','OpenBlade.DeploymentBroker','OpenBlade.Deployment','OpenBlade.Contracts','OpenBlade.Deployment.Tests','OpenBlade.Setup.Tests','OpenBlade.Service','OpenBlade.Session','OpenBlade.DeploymentClient','OpenBlade.CandidateReceipt.Scratch')) {
        if (-not ($rows | Where-Object { $_.layout -eq $layout -and $_.project -eq $name -and $_.variant -eq 'full' })) { throw "Missing reachable $name ($layout)" }
    }
}
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'evaluations.json')
Write-Output "PASS: $($rows.Count) actual reachable project evaluations; ordinary/artifacts; default and six distinct fixture graphs."
