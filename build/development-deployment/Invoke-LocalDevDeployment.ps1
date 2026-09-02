[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$TpmKeyName,
    [switch]$StageOnly,
    [switch]$ThermalExternalDriftValidation,
    [string]$ValidationOutputDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'LocalDevBuildVersion.ps1')
$head = (& git -C $repository rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') {
    throw 'The exact local-development Git source revision is unavailable.'
}

$changes = @(& git -C $repository status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
    throw 'Local-development deployment requires a clean committed worktree.'
}

[xml]$props = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
$baseVersion = [string]$props.Project.PropertyGroup.OpenBladeVersion
if ($baseVersion -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw 'Directory.Build.props does not contain a canonical OpenBlade version.'
}
$localDevVersion = Get-OpenBladeNextLocalDevBuildVersion

if ($ThermalExternalDriftValidation) {
    if ([string]::IsNullOrWhiteSpace($ValidationOutputDirectory)) {
        throw '-ValidationOutputDirectory is required for thermal external-drift validation.'
    }
    $validationBuildScript = Join-Path $repository `
        'build\Build-ThermalExternalDriftValidation.ps1'
    & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $validationBuildScript `
        -OutputDirectory $ValidationOutputDirectory `
        -LocalDevVersion $localDevVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The coordinated thermal validation build failed with exit code $LASTEXITCODE."
    }
    $installer = Join-Path ([IO.Path]::GetFullPath($ValidationOutputDirectory)) `
        'OpenBlade-Setup.exe'
}
else {
    if (-not [string]::IsNullOrWhiteSpace($ValidationOutputDirectory)) {
        throw '-ValidationOutputDirectory is valid only with -ThermalExternalDriftValidation.'
    }
    $publishScript = Join-Path $repository 'build\Publish.ps1'
    & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $publishScript `
        -Configuration Release `
        -BuildChannel LocalDev `
        -LocalDevVersion $localDevVersion `
        -SourceRevision $head
    if ($LASTEXITCODE -ne 0) {
        throw "The ordinary OpenBlade publish failed with exit code $LASTEXITCODE."
    }
    $installer = Join-Path $repository 'artifacts\win-x64\OpenBlade-Setup.exe'
}

$toolProject = Join-Path $repository (
    'build\tools\OpenBlade.LocalDevDeployment.Tool\OpenBlade.LocalDevDeployment.Tool.csproj')
& dotnet run `
    --project $toolProject `
    --configuration Release `
    --property:NuGetAudit=false `
    -- `
    stage `
    --installer $installer `
    --version $localDevVersion `
    --source-revision $head `
    --key-name $TpmKeyName
if ($LASTEXITCODE -ne 0) {
    throw "The TPM-signed local-development staging step failed with exit code $LASTEXITCODE."
}

if ($StageOnly) {
    Write-Host 'The fixed local-development inbox is staged; the broker was not invoked.'
    return
}

$client = Join-Path $env:ProgramFiles (
    'OpenBlade Local Dev Deployment Client\OpenBlade.DeploymentClient.exe')
if (-not (Test-Path -LiteralPath $client -PathType Leaf)) {
    throw 'The attended local-development deployment enrollment is not installed.'
}

& $client
if ($LASTEXITCODE -ne 0) {
    throw "The local-development broker client failed with exit code $LASTEXITCODE."
}

Write-Host (
    'Applied and relaunched local OpenBlade build {0} as {1} without another UAC prompt.' -f
        $head,
        $localDevVersion)
if ($ThermalExternalDriftValidation) {
    Write-Host (
        'Thermal external-drift validation evidence: {0}' -f
            ([IO.Path]::GetFullPath($ValidationOutputDirectory)))
}
