#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FixtureGraphIsolation.ps1')
$repository = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot))))
$EvidenceRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($EvidenceRoot))
Assert-ExternalFixtureEvidenceRoot $repository $EvidenceRoot
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
    installedStatus = @('OpenBladeNativeStatusIntegration', 'OpenBladeLocalDevDeployment')
    installedStatusNative = @('OpenBladeNativeStatusIntegration', 'OpenBladeLocalDevDeployment', 'OpenBladeNativePackageTests')
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
            $rootDestination = Join-Path $destination $name
            [IO.Directory]::CreateDirectory($rootDestination) | Out-Null
            $arguments[6] = "-p:FixtureEvaluationRoot=$rootDestination"
            & dotnet @arguments *> (Join-Path $EvidenceRoot ($layout + '-' + $variant.Key + '-' + $name + '.log'))
            if ($LASTEXITCODE -ne 0) { throw "Evaluation failed: $layout $($variant.Key) $name" }
        }
        foreach ($file in Get-ChildItem -LiteralPath $destination -Filter '*.txt' -Recurse) {
            $cells = (Get-Content -LiteralPath $file.FullName -Raw).Trim() -split '\|'
            $rows += [pscustomobject]@{ layout=$layout;variant=$variant.Key;root=$file.Directory.Name;project=$cells[0];target=$cells[1];intermediate=$cells[2];extensions=$cells[3];assets=$cells[4];identity=$cells[5];output=$cells[6];defines=$cells[7];references=$cells[8] }
        }
    }
}
# Keep every root observation. Collapse only after verifying exact agreement;
# no differing current root context has been established.
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'evaluations-by-root.json')
$rows = @(Get-AgreedFixtureGraphRows $rows)
Assert-FixtureGraphIsolation $rows
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'evaluations.json')
Write-Output "PASS: $($rows.Count) actual reachable project evaluations; ordinary/artifacts; default and eight distinct selected/fixture graphs."
