Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Packaging only. No product project references or loads this dormant library.
$script:OpenBladeNativeFileName = 'openblade_ffi.dll'
$script:OpenBladeNativeManifestName = 'OpenBlade.Native.provenance.json'

function Get-OpenBladeNativeInputs {
    param([Parameter(Mandatory)][string]$Repository)

    $paths = @(& git -C $Repository ls-files -- rust build/NativePayload.ps1 Directory.Build.props LICENSE)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -lt 10) { throw 'Cannot enumerate native source inputs.' }
    $inputs = @()
    foreach ($path in @($paths | Sort-Object -CaseSensitive)) {
        if ($path -notmatch '^[A-Za-z0-9_./-]+$' -or $path.Contains('..')) { throw 'Invalid native source path.' }
        $inputs += [ordered]@{
            path = $path
            sha256 = (Get-FileHash -LiteralPath (Join-Path $Repository $path) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return $inputs
}

function Get-OpenBladeNativePeInfo {
    param([Parameter(Mandatory)][string]$Path)

    # Inspect bytes without loading native code. Offsets are checked before reads.
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) { throw 'Invalid native PE image.' }
    $pe = [BitConverter]::ToInt32($bytes, 60)
    if ($pe -lt 64 -or $pe -gt $bytes.Length - 256 -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) {
        throw 'Invalid native PE header.'
    }
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $pe + 20)
    $optional = $pe + 24
    if ($machine -ne 0x8664 -or [BitConverter]::ToUInt16($bytes, $optional) -ne 0x20B -or
        ([BitConverter]::ToUInt16($bytes, $pe + 22) -band 0x2000) -eq 0 -or
        $optionalSize -lt 224 -or $sectionCount -lt 1 -or $sectionCount -gt 96 -or
        $optional + $optionalSize + 40 * $sectionCount -gt $bytes.Length) {
        throw 'Native payload must be a Windows x64 PE32+ DLL.'
    }
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $optional + $optionalSize + 40 * $index
        $sections += [pscustomobject]@{
            VirtualAddress = [uint64][BitConverter]::ToUInt32($bytes, $offset + 12)
            RawSize = [uint64][BitConverter]::ToUInt32($bytes, $offset + 16)
            RawOffset = [uint64][BitConverter]::ToUInt32($bytes, $offset + 20)
        }
    }
    function Resolve-NativeRva([uint64]$Rva, [int]$Length) {
        foreach ($section in $sections) {
            if ($Rva -ge $section.VirtualAddress -and $Rva - $section.VirtualAddress + $Length -le $section.RawSize) {
                $fileOffset = $section.RawOffset + $Rva - $section.VirtualAddress
                if ($fileOffset + $Length -le $bytes.Length) { return [int]$fileOffset }
            }
        }
        throw 'Invalid native PE RVA.'
    }
    $importRva = [BitConverter]::ToUInt32($bytes, $optional + 120)
    $importSize = [BitConverter]::ToUInt32($bytes, $optional + 124)
    if ($importSize -lt 20 -or $importSize -gt 65536) { throw 'Invalid native import table.' }
    $imports = @()
    $terminated = $false
    for ($index = 0; $index -lt [Math]::Floor($importSize / 20); $index++) {
        $offset = Resolve-NativeRva ($importRva + 20 * $index) 20
        $nameRva = [BitConverter]::ToUInt32($bytes, $offset + 12)
        if ($nameRva -eq 0) { $terminated = $true; break }
        $name = ''
        for ($character = 0; $character -lt 128; $character++) {
            $position = Resolve-NativeRva ($nameRva + $character) 1
            if ($bytes[$position] -eq 0) { break }
            $name += [char]$bytes[$position]
        }
        if ($character -eq 128 -or $name -notmatch '^(?i:kernel32|advapi32|bcrypt|ntdll|userenv|ws2_32)\.dll$') {
            throw "Native DLL has an unreviewed runtime dependency: $name"
        }
        $imports += $name.ToLowerInvariant()
    }
    if (-not $terminated -or $imports.Count -eq 0) { throw 'Invalid native import termination.' }
    # Delay-load dependencies are not part of the reviewed standalone contract.
    if ([BitConverter]::ToUInt32($bytes, $optional + 208) -ne 0) { throw 'Native delay imports are not admitted.' }
    return [pscustomobject]@{ Machine = 'x64'; Imports = @($imports | Sort-Object -Unique) }
}

function Assert-OpenBladeNativePayload {
    param(
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][psobject]$BuildIdentity,
        [switch]$RequireSignature
    )

    $library = Join-Path $Payload $script:OpenBladeNativeFileName
    $manifest = Get-Content -LiteralPath (Join-Path $Payload $script:OpenBladeNativeManifestName) -Raw | ConvertFrom-Json
    $pe = Get-OpenBladeNativePeInfo -Path $library
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($library)
    $head = & git -C $Repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify native source revision.' }
    $inputs = @(Get-OpenBladeNativeInputs -Repository $Repository)
    $pin = [regex]::Match((Get-Content (Join-Path $Repository 'rust/rust-toolchain.toml') -Raw), '(?m)^channel = "([0-9.]+)"$').Groups[1].Value
    if ($manifest.schemaVersion -ne 1 -or $manifest.abiVersion -ne '0x00010000' -or
        $manifest.fileName -cne $script:OpenBladeNativeFileName -or $manifest.target -cne 'x86_64-pc-windows-msvc' -or
        $manifest.sourceRevision -cne $head -or $manifest.productVersion -cne $BuildIdentity.Version -or
        $manifest.fileVersion -cne $BuildIdentity.FileVersion -or $version.FileVersion -cne $BuildIdentity.FileVersion -or
        $manifest.sha256 -cne (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $manifest.staticCrt -ne $true -or $manifest.panic -cne 'unwind' -or
        $manifest.compiler[0] -notlike "rustc $pin *" -or
        ($manifest.imports -join ',') -cne ($pe.Imports -join ',') -or
        (ConvertTo-Json -InputObject @($manifest.inputs) -Compress) -cne (ConvertTo-Json -InputObject $inputs -Compress)) {
        throw 'Native payload hash, identity, dependencies or source provenance does not match.'
    }
    $notice = Join-Path $Payload 'OpenBlade.Native.NOTICE.txt'
    if ($manifest.noticeSha256 -cne (Get-FileHash -LiteralPath $notice -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Native payload notice hash does not match.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $library
    if ($RequireSignature) {
        if ($signature.Status -ne 'Valid') { throw 'Native payload requires valid Authenticode signing.' }
    } elseif ($signature.Status -notin @('Valid', 'NotSigned')) { throw 'Native payload signature is invalid.' }
}

function Update-OpenBladeNativePayloadHash {
    param([Parameter(Mandatory)][string]$Payload)

    $manifestPath = Join-Path $Payload $script:OpenBladeNativeManifestName
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.sha256 = (Get-FileHash -LiteralPath (Join-Path $Payload $script:OpenBladeNativeFileName) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Build-OpenBladeNativePayload {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Output,
        [Parameter(Mandatory)][psobject]$BuildIdentity
    )

    if ($env:OS -ne 'Windows_NT') { throw 'Release native packaging requires Windows x64 MSVC tools.' }
    $head = & git -C $Repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Native packaging requires exact Git provenance.' }
    if ($BuildIdentity.SourceRevision -and $BuildIdentity.SourceRevision -cne $head) { throw 'Native source revision differs from build identity.' }
    $inputs = @(Get-OpenBladeNativeInputs -Repository $Repository)
    $paths = @($inputs | ForEach-Object { $_.path })
    & git -C $Repository diff --quiet HEAD -- @paths
    if ($LASTEXITCODE -ne 0) { throw 'Commit native build inputs before release packaging.' }
    $untracked = @(& git -C $Repository ls-files --others --exclude-standard -- rust build/NativePayload.ps1)
    if ($LASTEXITCODE -ne 0 -or $untracked.Count -ne 0) { throw 'Untracked native inputs cannot enter release packaging.' }
    $outputPath = [IO.Path]::GetFullPath($Output)
    $artifactRoot = Join-Path $Repository 'artifacts'
    Assert-StrictPublishDescendant -PermittedRoot $artifactRoot -Candidate $outputPath -Context 'native build output'
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

    # Restore the entire process environment, including VsDevCmd's changes.
    $savedEnvironment = @{}
    foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) { $savedEnvironment[$entry.Key] = $entry.Value }
    Push-Location (Join-Path $Repository 'rust')
    try {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'MSVC Build Tools with Windows SDK are required.' }
        $devcmd = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
        $environment = & $env:ComSpec /d /c "call `"$devcmd`" -no_logo -arch=x64 -host_arch=x64 >nul && set"
        if ($LASTEXITCODE -ne 0) { throw 'MSVC environment initialization failed.' }
        foreach ($line in $environment) {
            if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
        }
        $pin = [regex]::Match((Get-Content rust-toolchain.toml -Raw), '(?m)^channel = "([0-9.]+)"$').Groups[1].Value
        $compiler = & rustc "+$pin" -vV
        if ($LASTEXITCODE -ne 0 -or $compiler[0] -notlike "rustc $pin *") { throw 'Pinned Rust compiler is unavailable.' }
        $linker = (Get-Command link.exe -ErrorAction Stop).Source
        $linkerHash = (Get-FileHash -LiteralPath $linker -Algorithm SHA256).Hash.ToLowerInvariant()
        $resourceCompiler = (Get-Command rc.exe -ErrorAction Stop).Source
        $nativeBuild = Join-Path $outputPath 'build'
        New-Item -ItemType Directory -Path $nativeBuild -Force | Out-Null
        $resource = Join-Path $nativeBuild 'version.rc'
        $resourceObject = Join-Path $nativeBuild 'version.res'
        $numericVersion = $BuildIdentity.FileVersion.Replace('.', ',')
        $resourceText = @"
1 VERSIONINFO
FILEVERSION $numericVersion
PRODUCTVERSION $numericVersion
FILEFLAGSMASK 0x3fL
FILEFLAGS 0
FILEOS 0x40004L
FILETYPE 0x2L
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileDescription", "OpenBlade dormant native codec\0"
   VALUE "FileVersion", "$($BuildIdentity.FileVersion)\0"
   VALUE "ProductVersion", "$($BuildIdentity.InformationalVersion)\0"
   VALUE "OriginalFilename", "openblade_ffi.dll\0"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
        [IO.File]::WriteAllText($resource, $resourceText, [Text.Encoding]::ASCII)
        & $resourceCompiler /nologo "/fo$resourceObject" $resource
        if ($LASTEXITCODE -ne 0) { throw 'Native version resource compilation failed.' }
        $env:CARGO_TARGET_DIR = Join-Path $nativeBuild 'target'
        $flags = @('-C', 'target-feature=+crt-static', '-C', 'link-arg=/Brepro', "--remap-path-prefix=$Repository=/openblade")
        $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]31
        $env:RUSTFLAGS = $null
        $env:CARGO_INCREMENTAL = '0'
        & cargo "+$pin" rustc --frozen --release -p openblade-ffi --lib --target x86_64-pc-windows-msvc -- -C "link-arg=$resourceObject"
        if ($LASTEXITCODE -ne 0) { throw 'Native release build failed.' }
        $library = Join-Path $outputPath $script:OpenBladeNativeFileName
        Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'x86_64-pc-windows-msvc/release/openblade_ffi.dll') -Destination $library
        $pe = Get-OpenBladeNativePeInfo -Path $library
        $notice = @"
OpenBlade native codec - GPL-3.0-or-later
Source: https://github.com/OpenBladeProject/openblade-core/tree/$head/rust
The complete corresponding source and build instructions are in that revision.
Production crates use only local OpenBlade dependencies and the Rust standard library.
Rust standard library: MIT OR Apache-2.0; notices follow (from the pinned toolchain).
"@
        $sysroot = & rustc "+$pin" --print sysroot
        if ($LASTEXITCODE -ne 0) { throw 'Cannot locate pinned Rust license notices.' }
        $rustNotices = Join-Path $sysroot 'share/doc/rust'
        $notice += "`n`n--- Rust standard library copyright ---`n" + [IO.File]::ReadAllText((Join-Path $rustNotices 'COPYRIGHT-library.html'))
        foreach ($license in @(Get-ChildItem -LiteralPath (Join-Path $rustNotices 'licenses') -Filter '*.txt' | Sort-Object Name)) {
            $notice += "`n`n--- Rust $($license.Name) ---`n" + [IO.File]::ReadAllText($license.FullName)
        }
        $notice += "`n`n--- OpenBlade GPL-3.0-or-later ---`n" + [IO.File]::ReadAllText((Join-Path $Repository 'LICENSE'))
        [IO.File]::WriteAllText((Join-Path $outputPath 'OpenBlade.Native.NOTICE.txt'), $notice, [Text.UTF8Encoding]::new($false))
        $after = @(Get-OpenBladeNativeInputs -Repository $Repository)
        if ((ConvertTo-Json -InputObject $after -Compress) -cne (ConvertTo-Json -InputObject $inputs -Compress)) { throw 'Native inputs changed during build.' }
        $manifest = [ordered]@{
            schemaVersion = 1; abiVersion = '0x00010000'; fileName = $script:OpenBladeNativeFileName
            target = 'x86_64-pc-windows-msvc'; productVersion = $BuildIdentity.Version; fileVersion = $BuildIdentity.FileVersion
            sourceRevision = $head; compiler = @($compiler); linkerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($linker).FileVersion
            linkerSha256 = $linkerHash; resourceCompilerSha256 = (Get-FileHash $resourceCompiler -Algorithm SHA256).Hash.ToLowerInvariant()
            staticCrt = $true; panic = 'unwind'
            # Record reproducible arguments without a developer's absolute paths.
            flags = @('-C target-feature=+crt-static', '-C link-arg=/Brepro', '--remap-path-prefix=<repository>=/openblade', '-C link-arg=<generated-version-resource>')
            inputs = $inputs; imports = $pe.Imports
            unsignedSha256 = (Get-FileHash $library -Algorithm SHA256).Hash.ToLowerInvariant()
            sha256 = (Get-FileHash $library -Algorithm SHA256).Hash.ToLowerInvariant()
            noticeSha256 = (Get-FileHash (Join-Path $outputPath 'OpenBlade.Native.NOTICE.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        [IO.File]::WriteAllText((Join-Path $outputPath $script:OpenBladeNativeManifestName), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Assert-OpenBladeNativePayload -Payload $outputPath -Repository $Repository -BuildIdentity $BuildIdentity
    }
    finally {
        Pop-Location
        foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
            if (-not $savedEnvironment.ContainsKey($key)) { [Environment]::SetEnvironmentVariable($key, $null, 'Process') }
        }
        foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    }
}
