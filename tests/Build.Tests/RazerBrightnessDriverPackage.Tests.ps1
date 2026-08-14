[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exporterPath = Join-Path $repository 'build\Export-RazerBrightnessDriverPackage.ps1'
$transportPath = Join-Path $repository (
    'src\OpenBlade.Service\WindowsRazerDisplayBrightnessHidTransport.cs')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsContaining {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Expected
    )

    try {
        & $Action
    }
    catch {
        Assert-True ($_.Exception.Message.IndexOf(
                $Expected,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) `
            "Expected error containing '$Expected', got '$($_.Exception.Message)'."
        return
    }

    throw "Expected an error containing '$Expected'."
}

$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $exporterPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) `
    'The Razer driver package exporter must parse under Windows PowerShell.'

$source = Get-Content -Raw -LiteralPath $exporterPath
$transport = Get-Content -Raw -LiteralPath $transportPath

foreach ($originalInf in @(
        'rzdevu_02e0_dkm.inf',
        'rzdevu_02e0_vcon.inf',
        'rzcommonu.inf',
        'rzdevu_02e0_kbd.inf',
        'rzdevu_02e0_mou.inf')) {
    Assert-True ($source.IndexOf($originalInf, [StringComparison]::Ordinal) -ge 0) `
        "The exporter is missing the complete-model package '$originalInf'."
}

foreach ($machineLocalInf in @(
        'oem176.inf', 'oem178.inf', 'oem179.inf', 'oem180.inf', 'oem181.inf')) {
    Assert-True ($source.IndexOf(
            $machineLocalInf,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "The exporter must not pin machine-local identity '$machineLocalInf'."
}

foreach ($contract in @(
        '/export-driver',
        'Get-AuthenticodeSignature',
        '[Management.Automation.SignatureStatus]::Valid',
        'Microsoft Windows Hardware Compatibility Publisher',
        'does not contain its exact three-file set',
        'The package directory name must be',
        'The output directory already exists',
        'The archive already exists',
        'The archive must be outside the package directory',
        'THIRD-PARTY-NOTICE.txt',
        'SHA256SUMS.txt')) {
    Assert-True ($source.IndexOf($contract, [StringComparison]::Ordinal) -ge 0) `
        "The exporter is missing safety contract '$contract'."
}

foreach ($runtimeHash in @(
        '64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59',
        'BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787')) {
    Assert-True ($source.IndexOf($runtimeHash, [StringComparison]::Ordinal) -ge 0) `
        "The exporter is missing admitted runtime hash '$runtimeHash'."
    Assert-True ($transport.IndexOf($runtimeHash, [StringComparison]::Ordinal) -ge 0) `
        "The transport and exporter disagree on runtime hash '$runtimeHash'."
}

. $exporterPath

$fixtureRoot = Join-Path (
    [IO.Path]::GetTempPath()) ('openblade-driver-package-fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -ErrorAction Stop | Out-Null
try {
    $hashFixture = Join-Path $fixtureRoot 'hash.bin'
    [IO.File]::WriteAllBytes($hashFixture, [byte[]](1, 2, 3, 4))
    $expectedHash = (Get-FileHash -LiteralPath $hashFixture -Algorithm SHA256).Hash
    Assert-ExactHash -Path $hashFixture -Expected $expectedHash
    Assert-ThrowsContaining `
        -Expected 'expected' `
        -Action { Assert-ExactHash -Path $hashFixture -Expected ('0' * 64) }
    Assert-ThrowsContaining `
        -Expected 'exact admitted Microsoft hardware signature' `
        -Action {
            Assert-ExactMicrosoftSignature `
                -Path $hashFixture `
                -ExpectedThumbprint ('0' * 40)
        }

    $fileSetRoot = Join-Path $fixtureRoot 'file-set'
    New-Item -ItemType Directory -Path $fileSetRoot -ErrorAction Stop | Out-Null
    foreach ($name in 'sample.inf', 'sample.cat', 'sample.sys') {
        [IO.File]::WriteAllText((Join-Path $fileSetRoot $name), $name)
    }
    Assert-ExactPackageFileSet `
        -PackageRoot $fileSetRoot `
        -ExpectedFiles @('sample.inf', 'sample.cat', 'sample.sys') `
        -OriginalInf 'sample.inf'
    $nested = Join-Path $fileSetRoot 'nested'
    New-Item -ItemType Directory -Path $nested -ErrorAction Stop | Out-Null
    [IO.File]::WriteAllText((Join-Path $nested 'extra.bin'), 'extra')
    Assert-ThrowsContaining `
        -Expected 'exact three-file set' `
        -Action {
            Assert-ExactPackageFileSet `
                -PackageRoot $fileSetRoot `
                -ExpectedFiles @('sample.inf', 'sample.cat', 'sample.sys') `
                -OriginalInf 'sample.inf'
        }
    Remove-Item -LiteralPath $nested -Recurse -Force -ErrorAction Stop
    $unexpected = Join-Path $fileSetRoot 'unexpected.bin'
    [IO.File]::WriteAllText($unexpected, 'unexpected')
    Assert-ThrowsContaining `
        -Expected 'exact three-file set' `
        -Action {
            Assert-ExactPackageFileSet `
                -PackageRoot $fileSetRoot `
                -ExpectedFiles @('sample.inf', 'sample.cat', 'sample.sys') `
                -OriginalInf 'sample.inf'
        }
    Remove-Item -LiteralPath $unexpected -Force -ErrorAction Stop
    Remove-Item -LiteralPath (Join-Path $fileSetRoot 'sample.sys') -Force -ErrorAction Stop
    Assert-ThrowsContaining `
        -Expected 'exact three-file set' `
        -Action {
            Assert-ExactPackageFileSet `
                -PackageRoot $fileSetRoot `
                -ExpectedFiles @('sample.inf', 'sample.cat', 'sample.sys') `
                -OriginalInf 'sample.inf'
        }

    $zipSourceOne = Join-Path $fixtureRoot 'zip-source-one'
    $zipSourceTwo = Join-Path $fixtureRoot 'zip-source-two'
    New-Item -ItemType Directory -Path $zipSourceOne,$zipSourceTwo -ErrorAction Stop | Out-Null
    foreach ($sourceRoot in $zipSourceOne,$zipSourceTwo) {
        [IO.File]::WriteAllText((Join-Path $sourceRoot 'a.txt'), "alpha`n")
        New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'nested') -ErrorAction Stop |
            Out-Null
        [IO.File]::WriteAllBytes(
            (Join-Path $sourceRoot 'nested\b.bin'),
            [byte[]](9, 8, 7))
    }
    $zipOne = Join-Path $fixtureRoot 'one.zip'
    $zipTwo = Join-Path $fixtureRoot 'two.zip'
    New-DeterministicZip `
        -SourceDirectory $zipSourceOne `
        -DestinationPath $zipOne `
        -ArchiveRootName 'fixed-root'
    New-DeterministicZip `
        -SourceDirectory $zipSourceTwo `
        -DestinationPath $zipTwo `
        -ArchiveRootName 'fixed-root'
    Assert-True ([string]::Equals(
            (Get-FileSha256 -Path $zipOne),
            (Get-FileSha256 -Path $zipTwo),
            [StringComparison]::Ordinal)) `
        'Identical fixture content must produce the same Windows PowerShell 5.1 ZIP.'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipOne)
    try {
        $entryNames = @($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        Assert-True (($entryNames -join '|') -eq
            'fixed-root/a.txt|fixed-root/nested/b.bin') `
            'The ZIP must contain only the expected normalized relative paths.'
    }
    finally {
        $zip.Dispose()
    }

    $atomicDirectory = Join-Path $fixtureRoot 'atomic-output'
    $atomicArchive = Join-Path $fixtureRoot 'atomic.zip'
    Invoke-AtomicPackageBuild `
        -FinalDirectory $atomicDirectory `
        -FinalArchive $atomicArchive `
        -BuildAction {
            param($stagingDirectory, $stagingArchive)
            [IO.File]::WriteAllText((Join-Path $stagingDirectory 'complete.txt'), 'complete')
            [IO.File]::WriteAllText($stagingArchive, 'archive')
        }
    Assert-True (Test-Path -LiteralPath $atomicDirectory -PathType Container) `
        'The atomic builder did not publish its completed directory.'
    Assert-True (Test-Path -LiteralPath $atomicArchive -PathType Leaf) `
        'The atomic builder did not publish its completed archive.'
    Remove-Item -LiteralPath $atomicDirectory -Recurse -Force -ErrorAction Stop
    Remove-Item -LiteralPath $atomicArchive -Force -ErrorAction Stop

    $failedDirectory = Join-Path $fixtureRoot 'failed-output'
    $failedArchive = Join-Path $fixtureRoot 'failed.zip'
    Assert-ThrowsContaining `
        -Expected 'fixture failure' `
        -Action {
            Invoke-AtomicPackageBuild `
                -FinalDirectory $failedDirectory `
                -FinalArchive $failedArchive `
                -BuildAction {
                    param($stagingDirectory, $stagingArchive)
                    [IO.File]::WriteAllText(
                        (Join-Path $stagingDirectory 'partial.txt'),
                        'partial')
                    [IO.File]::WriteAllText($stagingArchive, 'partial')
                    throw 'fixture failure'
                }
        }
    Assert-True (-not (Test-Path -LiteralPath $failedDirectory)) `
        'A failed build must not publish its package directory.'
    Assert-True (-not (Test-Path -LiteralPath $failedArchive)) `
        'A failed build must not publish its archive.'
    $stagingDebris = @(Get-ChildItem -LiteralPath $fixtureRoot -Force |
        Where-Object { $_.Name.IndexOf('.staging-', [StringComparison]::Ordinal) -ge 0 })
    Assert-True ($stagingDebris.Count -eq 0) `
        'A failed build must remove the exact staging paths it created.'
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction Stop
}

$testParent = Join-Path (
    [IO.Path]::GetTempPath()) ('openblade-driver-export-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testParent -ErrorAction Stop | Out-Null
$existingOutput = Join-Path $testParent 'Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76'
New-Item -ItemType Directory -Path $existingOutput -ErrorAction Stop | Out-Null
try {
    Assert-ThrowsContaining `
        -Expected 'output directory already exists' `
        -Action { & $exporterPath -OutputDirectory $existingOutput }
}
finally {
    Remove-Item -LiteralPath $testParent -Recurse -Force -ErrorAction Stop
}

$missingParent = Join-Path (
    [IO.Path]::GetTempPath()) ('openblade-driver-export-test-' + [Guid]::NewGuid().ToString('N'))
$missingOutput = Join-Path $missingParent 'Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76'
Assert-ThrowsContaining `
    -Expected 'PnPUtil is missing' `
    -Action {
        & $exporterPath `
            -OutputDirectory $missingOutput `
            -PnPUtil (Join-Path $missingOutput 'missing-pnputil.exe')
    }
Assert-True (-not (Test-Path -LiteralPath $missingOutput)) `
    'A failed preflight must not create the package directory.'

$repositoryOutput = Join-Path $repository 'Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76'
Assert-ThrowsContaining `
    -Expected 'outside the repository' `
    -Action {
        Invoke-RazerBrightnessDriverPackageExport `
            -RequestedOutputDirectory $repositoryOutput `
            -ResolvedPnPUtil (Join-Path $env:WINDIR 'System32\pnputil.exe')
    }

$equalityParent = Join-Path (
    [IO.Path]::GetTempPath()) ('openblade-driver-export-test-' + [Guid]::NewGuid().ToString('N'))
$equalityOutput = Join-Path $equalityParent 'Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76'
Assert-ThrowsContaining `
    -Expected 'archive must be outside' `
    -Action {
        Invoke-RazerBrightnessDriverPackageExport `
            -RequestedOutputDirectory $equalityOutput `
            -RequestedArchivePath $equalityOutput `
            -ResolvedPnPUtil (Join-Path $env:WINDIR 'System32\pnputil.exe')
    }
Assert-True (-not (Test-Path -LiteralPath $equalityParent)) `
    'A failed equality preflight must not create output paths.'

Write-Output 'Razer brightness driver package contracts passed.'
