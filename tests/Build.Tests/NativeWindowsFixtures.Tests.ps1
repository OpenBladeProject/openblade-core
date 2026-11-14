[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
& $python -B (Join-Path $repository 'build/test_native_windows_fixtures.py')
if ($LASTEXITCODE -ne 0) { throw "Native Windows fixture harness contracts failed: $LASTEXITCODE" }
