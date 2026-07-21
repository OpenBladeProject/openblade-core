[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixtures = @(
    [pscustomobject]@{
        Path = 'src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_TidalOracle.json'
        Sha256 = '35D69EB780ED6727951E630565D2873FAECE7B47450CB4B30AFAE38AC3271C4F'
    },
    [pscustomobject]@{
        Path = 'src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_WaveWheelOracle.json'
        Sha256 = 'CFC9B9365CF7C55069AD2782AE510F0838865EAB0681E7A45BAD1D9B932B432E'
    }
)

foreach ($fixture in $fixtures) {
    $fullPath = Join-Path $repository $fixture.Path
    $attribute = & git -C $repository check-attr eol -- $fixture.Path
    if ($LASTEXITCODE -ne 0 -or $attribute -notmatch ': eol: crlf$') {
        throw "The approved lighting fixture must be checked out with CRLF bytes: $($fixture.Path)"
    }

    $bytes = [IO.File]::ReadAllBytes($fullPath)
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    if ($text -notmatch "`r`n" -or $text -match '(?<!\r)\n') {
        throw "The approved lighting fixture has mixed or non-CRLF line endings: $($fixture.Path)"
    }

    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if ($actualHash -cne $fixture.Sha256) {
        throw "The approved lighting fixture hash changed: $($fixture.Path) is $actualHash"
    }
}

Write-Host 'Lighting oracle checkout-policy tests passed.'
