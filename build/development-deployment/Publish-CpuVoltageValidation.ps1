[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$LocalDevVersion
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Copy-OpenBladeCpuVoltageClosure {
    param(
        [Parameter(Mandatory)][string]$Capture,
        [Parameter(Mandatory)][string]$Smoke,
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Source
    )
    if (Test-Path -LiteralPath $Destination) { throw 'A fresh closure directory is required.' }
    $files = [Collections.Generic.List[object]]::new()
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$total = 0
    foreach ($role in @(
        @{ Root=$Capture; Prefix='' },
        @{ Root=$Smoke; Prefix='firmware-smoke/' },
        @{ Root=$Payload; Prefix='service/' })) {
        $root = [IO.Path]::GetFullPath($role.Root).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Missing closure role: $root" }
        $pending = [Collections.Generic.Queue[string]]::new()
        $pending.Enqueue($root)
        while ($pending.Count -gt 0) {
            $directory = $pending.Dequeue()
            if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Closure directories must not be reparse points.'
            }
            foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Closure entries must not be reparse points.'
                }
                $relative = $entry.FullName.Substring($root.Length + 1).Replace('\', '/')
                # Match the canonical installed runtime exactly; only product UI assets are excluded.
                if ($role.Prefix -eq 'service/' -and ($relative -eq 'Assets' -or
                    $relative.StartsWith('Assets/', [StringComparison]::Ordinal) -or $relative -cin @(
                        'Microsoft.UI.Xaml/Assets/map.html',
                        'Microsoft.UI.Xaml/Assets/NoiseAsset_256x256_PNG.png'))) { continue }
                if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName); continue }
                $name = $role.Prefix + $relative
                if ($name.Length -gt 240 -or $name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*){0,2}$' -or
                    $name.Split('/') -contains '..' -or -not $names.Add($name)) {
                    throw "Noncanonical or duplicate closure name: $name"
                }
                if ($entry.Length -lt 0 -or $entry.Length -gt 134217728) { throw "Closure file exceeds its bound: $name" }
                $total += $entry.Length
                if ($files.Count -ge 1536 -or $total -gt 1073741824) { throw 'Closure exceeds its file or total-byte bound.' }
                $target = Join-Path $Destination $name.Replace('/', '\')
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                Copy-Item -LiteralPath $entry.FullName -Destination $target
                $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                if ((Get-Item -LiteralPath $target).Length -ne $entry.Length -or
                    $hash -ne (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash) {
                    throw "Closure copy changed: $name"
                }
                $files.Add([ordered]@{ name=$name; length=$entry.Length; sha256=$hash })
            }
        }
    }
    foreach ($required in @('OpenBlade.Capture.exe', 'OpenBlade.Capture.dll',
        'firmware-smoke/OpenBlade.FirmwareSmoke.exe', 'firmware-smoke/OpenBlade.FirmwareSmoke.dll',
        'service/OpenBlade.Service.exe', 'service/OpenBlade.Service.dll')) {
        if (-not $names.Contains($required)) { throw "Missing closure entrypoint: $required" }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
        version=1; source=$Source; files=@($files | Sort-Object { $_.name })
    } | ConvertTo-Json -Depth 5 -Compress))
    if ($bytes.Length -gt 524288) { throw 'Closure manifest exceeds 512 KiB.' }
    $manifest = Join-Path $Destination 'manifest.json'
    [IO.File]::WriteAllBytes($manifest, $bytes)
    [pscustomobject]@{ ManifestSha256=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash;
        ClosureFiles=$files.Count; ClosureBytes=$total }
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$') { throw 'Exact committed source is unavailable.' }
$dirty = @(& git -C $repository status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'CPU voltage validation requires a clean committed worktree.' }
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($output.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must be outside the source repository.'
}
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or
        @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) { throw 'Validation output must be an empty directory.' }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
. (Join-Path $repository 'build\BuildIdentity.ps1')
[xml]$props = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
$identity = Resolve-OpenBladeBuildIdentity -BaseVersion ([string]$props.Project.PropertyGroup.OpenBladeVersion) `
    -Channel LocalDev -LocalDevVersion $LocalDevVersion -SourceRevision $source
$serviceVersion = "$LocalDevVersion+voltage-validation.$($source.Substring(0, 8))"
if ($serviceVersion.Length -gt 64) { throw 'Service version exceeds the IPC limit.' }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repository 'build\Publish.ps1') `
    -Configuration Release -BuildChannel LocalDev -LocalDevVersion $LocalDevVersion `
    -SourceRevision $source -CpuVoltageValidation
if ($LASTEXITCODE -ne 0) { throw "CPU voltage installer publish failed with exit code $LASTEXITCODE." }
$smoke = Join-Path $output 'firmware-smoke'
& dotnet publish (Join-Path $repository 'src\OpenBlade.FirmwareSmoke\OpenBlade.FirmwareSmoke.csproj') `
    --configuration Release --runtime win-x64 --self-contained true --output $smoke `
    --property:NuGetAudit=false --property:PublishTrimmed=false `
    --property:OpenBladePadlessHyperBoostLifecycleValidation=false `
    --property:OpenBladeThermalExternalDriftValidation=false --property:OpenBladeCpuVoltageValidation=true `
    "--property:SourceRevision=$source" "--property:Version=$($identity.Version)" `
    "--property:AssemblyVersion=$($identity.AssemblyVersion)" "--property:FileVersion=$($identity.FileVersion)" `
    "--property:InformationalVersion=$serviceVersion" --property:IncludeSourceRevisionInInformationalVersion=false `
    --property:DebugType=None --property:DebugSymbols=false `
    '--property:SatelliteResourceLanguages=en-US%3Bzh-CN%3Bru-RU' --property:OpenBladeIncludePseudoLocalization=false
if ($LASTEXITCODE -ne 0) { throw "CPU voltage FirmwareSmoke publish failed with exit code $LASTEXITCODE." }
$artifacts = Join-Path $repository 'artifacts\win-x64'
$capture = Join-Path $artifacts 'OpenBlade.DeveloperTools'
$payload = Join-Path $artifacts 'OpenBlade\payload'
foreach ($assembly in @((Join-Path $capture 'OpenBlade.Capture.dll'),
    (Join-Path $smoke 'OpenBlade.FirmwareSmoke.dll'), (Join-Path $payload 'OpenBlade.Service.dll'))) {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion -cne $serviceVersion) {
        throw "CPU validation product version mismatch: $assembly"
    }
}
$closure = Copy-OpenBladeCpuVoltageClosure -Capture $capture -Smoke $smoke -Payload $payload `
    -Destination (Join-Path $output 'closure') -Source $source
$installer = Join-Path $output 'OpenBlade-Setup.exe'
Copy-Item -LiteralPath (Join-Path $artifacts 'OpenBlade-Setup.exe') -Destination $installer
[ordered]@{ Source=$source; ServiceVersion=$serviceVersion; ManifestSha256=$closure.ManifestSha256;
    InstallerSha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;
    ClosureFiles=$closure.ClosureFiles; ClosureBytes=$closure.ClosureBytes; Installed=$false; HardwareInvoked=$false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'cpu-voltage-validation-build.json') -Encoding UTF8
Write-Host "CPU voltage validation package created at $output"
