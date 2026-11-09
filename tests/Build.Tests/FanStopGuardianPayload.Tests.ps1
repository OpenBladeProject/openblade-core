[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build/PublishPayloadValidation.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.GuardianPayload.' + [Guid]::NewGuid().ToString('N'))
$guardian = Join-Path $root 'guardian'
$payload = Join-Path $root 'payload'
$owned = @('OpenBlade.FanStop.Guardian.exe', 'OpenBlade.FanStop.Guardian.dll',
    'OpenBlade.FanStop.Guardian.deps.json', 'OpenBlade.FanStop.Guardian.runtimeconfig.json')
try {
    [void][IO.Directory]::CreateDirectory($guardian)
    [void][IO.Directory]::CreateDirectory($payload)
    foreach ($name in $owned) { [IO.File]::WriteAllText((Join-Path $guardian $name), 'helper') }
    [IO.File]::WriteAllText((Join-Path $guardian 'OpenBlade.Service.dll'), 'shared')
    [IO.File]::WriteAllText((Join-Path $payload 'OpenBlade.Service.dll'), 'different')
    $caught = $false
    try { Add-GuardianPublishPayload -Guardian $guardian -Payload $payload }
    catch { $caught = $_.Exception.Message -like '*shared dependency differs*' }
    if (-not $caught -or (Get-ChildItem -LiteralPath $payload -File).Count -ne 1) {
        throw 'A conflicting shared dependency was accepted or partially copied.'
    }
    [IO.File]::WriteAllText((Join-Path $payload 'OpenBlade.Service.dll'), 'shared')
    [IO.File]::WriteAllText((Join-Path $guardian 'Unexpected.dll'), 'unexpected')
    $caught = $false
    try { Add-GuardianPublishPayload -Guardian $guardian -Payload $payload }
    catch { $caught = $_.Exception.Message -like '*shared dependency differs*' }
    if (-not $caught -or (Get-ChildItem -LiteralPath $payload -File).Count -ne 1) {
        throw 'An unreviewed helper dependency was accepted.'
    }
    Remove-Item -LiteralPath (Join-Path $guardian 'Unexpected.dll')
    Add-GuardianPublishPayload -Guardian $guardian -Payload $payload
    if ((Get-ChildItem -LiteralPath $payload -File).Count -ne 5 -or
        [IO.File]::ReadAllText((Join-Path $payload 'OpenBlade.Service.dll')) -ne 'shared') {
        throw 'Guardian payload merge changed the service or omitted helper files.'
    }
    Write-Output 'Fan guardian payload tests passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $expected = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\OpenBlade.GuardianPayload.'
    if (-not $resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary test root.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
