[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Read-Ast([string]$Relative) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repository $Relative), [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) "$Relative must parse."
    return $ast
}
$wrapper = Read-Ast 'build/development-deployment/Invoke-LocalDevDeployment.ps1'
$publisher = Read-Ast 'build/development-deployment/Publish-CpuVoltageValidation.ps1'
$copy = $publisher.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-OpenBladeCpuVoltageClosure'
}, $true)
. ([scriptblock]::Create($copy.Extent.Text))

# Exercise actual argument construction; no signer, client, installer or build is invoked.
$statements = $wrapper.EndBlock.Statements
$gates = @($statements | Where-Object { $_.Extent.Text.StartsWith('if ($CpuVoltageValidation -and') })
$gate = [scriptblock]::Create(($gates.Extent.Text -join "`n"))
foreach ($scenario in @('missing-output', 'combined', 'cpu', 'ordinary')) {
    $CpuVoltageValidation = $scenario -ne 'ordinary'
    $ThermalExternalDriftValidation = $scenario -eq 'combined'
    $ValidationOutputDirectory = if ($scenario -eq 'missing-output') { '' } else { 'C:\validation-output' }
    $rejected = $false
    try { . $gate } catch { $rejected = $true }
    Assert-True ($rejected -eq ($scenario -in @('missing-output', 'combined'))) "Incorrect wrapper admission: $scenario"
}
$stage = @($statements | Where-Object {
    $_.Extent.Text.StartsWith('$stageArguments =') -or $_.Extent.Text.Contains("`$stageArguments[0] = 'stage-cpu-voltage'")
})
$installer = 'C:\validation-output\OpenBlade-Setup.exe'
$head = '1' * 40; $TpmKeyName = 'enrolled-test-key'; $localDevVersion = '65535.1.2.3'
foreach ($enabled in @($false, $true)) {
    $CpuVoltageValidation = $enabled
    . ([scriptblock]::Create(($stage.Extent.Text -join "`n")))
    $expected = @($(if ($enabled) { 'stage-cpu-voltage' } else { 'stage' }), '--installer', $installer,
        '--version', $localDevVersion, '--source-revision', $head, '--key-name', $TpmKeyName)
    if ($enabled) { $expected += '--closure', 'C:\validation-output\closure' }
    Assert-True (($stageArguments -join '|') -ceq ($expected -join '|')) 'Stage arguments differ from the fixed signing contract.'
}
$dispatch = $statements | Where-Object { $_.Extent.Text.Contains('& $client --cpu-voltage') }
function Test-FixedClient { $script:clientArgs = @($args); $global:LASTEXITCODE = 0 }
$client = 'Test-FixedClient'
foreach ($enabled in @($false, $true)) {
    $CpuVoltageValidation = $enabled
    . ([scriptblock]::Create($dispatch.Extent.Text))
    Assert-True (($clientArgs -join '|') -ceq $(if ($enabled) { '--cpu-voltage' } else { '' })) 'Unexpected client authority.'
}
$source = $publisher.Extent.Text
Assert-True ($source.Contains('-SourceRevision $source -CpuVoltageValidation') -and
    $source.Contains('--property:OpenBladeCpuVoltageValidation=true') -and
    $source.Contains('--property:InformationalVersion=$serviceVersion') -and
    $source.Contains('ProductVersion -cne $serviceVersion')) 'Publisher must coordinate exact profile versions.'
Assert-True (-not ($source -match 'Start-Process|RunAs|cpu-voltage-session-client|cpu-voltage-owned-smoke')) 'Publisher must not elevate or submit jobs.'

$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('openblade-voltage-closure-' + [guid]::NewGuid().ToString('N'))))
try {
    function Write-Fixture([string]$Name) {
        $path = Join-Path $temporary $Name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, $Name)
    }
    foreach ($name in @('capture/OpenBlade.Capture.exe', 'capture/OpenBlade.Capture.dll',
        'smoke/OpenBlade.FirmwareSmoke.exe', 'smoke/OpenBlade.FirmwareSmoke.dll',
        'payload/OpenBlade.Service.exe', 'payload/OpenBlade.Service.dll', 'payload/OpenBlade.Tray.dll',
        'payload/ru-RU/Runtime.resources.dll', 'payload/Microsoft.UI.Xaml.dll', 'payload/Assets/Icon.png',
        'payload/Microsoft.UI.Xaml/Assets/map.html', 'payload/Microsoft.UI.Xaml/Assets/NoiseAsset_256x256_PNG.png')) {
        Write-Fixture $name
    }
    $parameters = @{ Capture=(Join-Path $temporary 'capture'); Smoke=(Join-Path $temporary 'smoke');
        Payload=(Join-Path $temporary 'payload'); Source=('1' * 40) }
    $destination = Join-Path $temporary 'complete'
    $result = Copy-OpenBladeCpuVoltageClosure @parameters -Destination $destination
    $manifest = Get-Content -LiteralPath (Join-Path $destination 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($result.ClosureFiles -eq 9 -and $manifest.source -ceq ('1' * 40)) 'Closure inventory/source changed.'
    Assert-True ($manifest.files.name -ccontains 'service/OpenBlade.Tray.dll' -and
        $manifest.files.name -ccontains 'service/ru-RU/Runtime.resources.dll' -and
        @($manifest.files.name | Where-Object { $_ -like '*Assets*' }).Count -eq 0) 'Canonical runtime or exact UI exclusion changed.'
    foreach ($file in $manifest.files) {
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $destination $file.name)).Hash -ceq $file.sha256 -and
            (Get-Item -LiteralPath (Join-Path $destination $file.name)).Length -eq $file.length) 'Manifest does not describe copied bytes.'
    }
    $failed = $false
    try { Copy-OpenBladeCpuVoltageClosure @parameters -Destination $destination | Out-Null } catch { $failed = $true }
    Assert-True $failed 'Existing closure must not be overwritten.'
    Write-Fixture 'payload/a/b/c.dll'
    $failed = $false
    try { Copy-OpenBladeCpuVoltageClosure @parameters -Destination (Join-Path $temporary 'deep') | Out-Null } catch { $failed = $true }
    Assert-True $failed 'Unsupported manifest depth must be rejected.'
    Remove-Item -LiteralPath (Join-Path $temporary 'payload/a/b/c.dll')
    for ($index = 0; $index -lt 1536; $index++) { Write-Fixture "capture/fixture$index.dll" }
    $failed = $false
    try { Copy-OpenBladeCpuVoltageClosure @parameters -Destination (Join-Path $temporary 'oversized') | Out-Null } catch { $failed = $true }
    Assert-True $failed 'File capacity must fail before signing.'
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $temporary.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
Write-Host "CPU voltage deployment script checks passed: $checks"
