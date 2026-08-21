[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$validator = Join-Path $repository 'build\Validate-LocalizationResources.ps1'

foreach ($project in @(
        'src\OpenBlade.Tray.Core\OpenBlade.Tray.Core.csproj',
        'src\OpenBlade.Setup\OpenBlade.Setup.csproj',
        'build\installer\OpenBlade.Installer.UI\OpenBlade.Installer.UI.csproj')) {
    [xml]$projectDocument = Get-Content (Join-Path $repository $project) -Raw
    $pseudoRemoval = @($projectDocument.Project.ItemGroup |
        Where-Object {
            $_.GetAttribute('Condition') -eq
                "'`$(OpenBladeIncludePseudoLocalization)' == 'false'"
        } |
        ForEach-Object EmbeddedResource |
        Where-Object {
            $_.GetAttribute('Remove') -eq 'Properties\Resources.qps-ploc.resx'
        })
    if ($pseudoRemoval.Count -ne 1) {
        throw "$project must exclude its qps-ploc satellite from production publishing."
    }
}

$publishSource = [IO.File]::ReadAllText((Join-Path $repository 'build\Publish.ps1'))
if ([regex]::Matches(
        $publishSource,
        '--property:OpenBladeIncludePseudoLocalization=false').Count -ne 2) {
    throw 'Production publish must suppress pseudo localization for product and installer UI payloads.'
}
if ($publishSource.IndexOf(
        '--property:SatelliteResourceLanguages=en-US%3Bzh-CN%3Bru-RU',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Production publish must retain every supported managed localization satellite.'
}
$affectedCiSource = [IO.File]::ReadAllText((
    Join-Path $repository 'build\Run-AffectedCi.ps1'))
if ($affectedCiSource.IndexOf(
        '--property:SatelliteResourceLanguages=en-US%3Bzh-CN%3Bru-RU',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Affected component publishing must retain every supported localization satellite.'
}

function Get-FixtureRelativePath {
    param([string]$Root, [string]$Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture source is outside the repository: $fullPath"
    }
    return $fullPath.Substring($prefix.Length)
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $validator,
    [ref]$null,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Localization validator contains parser errors: $($parseErrors -join '; ')"
}

& $validator -Repository $repository

$mainViewModelSource = [IO.File]::ReadAllText(
    (Join-Path $repository 'src\OpenBlade.Tray.Core\MainViewModel.cs'),
    [Text.UTF8Encoding]::new($false, $true))
if ($mainViewModelSource -match 'ToLower\(CultureInfo\.CurrentUICulture\)') {
    throw 'Localized presentation fragments must preserve translator-owned casing.'
}

$utf8 = [Text.UTF8Encoding]::new($false, $true)
$trayEnglish = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'), $utf8)
$trayPseudo = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'src\OpenBlade.Tray\Strings\qps-ploc\Resources.resw'), $utf8)
$installerEnglish = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx'), $utf8)
$installerPseudo = [xml][IO.File]::ReadAllText((
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx'), $utf8)

if (@($trayEnglish.root.data).Count -lt 400) {
    throw 'The tray en-US catalog unexpectedly lost critical-page coverage.'
}
if (@($trayEnglish.root.data).Count -ne @($trayPseudo.root.data).Count) {
    throw 'The tray pseudo catalog must match en-US key count.'
}
if (@($installerEnglish.root.data).Count -lt 45) {
    throw 'The installer en-US catalog unexpectedly lost interactive-state coverage.'
}
if (@($installerEnglish.root.data).Count -ne @($installerPseudo.root.data).Count) {
    throw 'The installer pseudo catalog must match en-US key count.'
}

function New-LocalizationFixture {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) (
        'OpenBlade.Localization.' + [Guid]::NewGuid().ToString('N'))
    foreach ($source in @(Get-ChildItem (Join-Path $repository 'src\OpenBlade.Tray') `
            -File -Recurse | Where-Object {
                $_.FullName -notmatch '\\(?:bin|obj)\\' -and
                ($_.Extension -in @('.cs', '.xaml', '.resw'))
            })) {
        $relative = Get-FixtureRelativePath $repository $source.FullName
        $destination = Join-Path $fixture $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $source.FullName -Destination $destination
    }
    foreach ($relative in @(
            'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml',
            'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml.cs',
            'build\installer\OpenBlade.Installer.UI\OpenBladeBootstrapperApplication.cs',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.zh-CN.resx',
            'build\installer\OpenBlade.Installer.UI\Properties\Resources.ru-RU.resx',
            'build\installer\Bundle.en-US.wxl',
            'build\installer\Bundle.wxs',
            'build\Extract-InstallerLocalization.ps1',
            'build\Extract-WinUiLocalization.ps1',
            'build\Update-LocalizationResources.ps1',
            'build\Update-PseudoLocalization.ps1',
            'src\OpenBlade.Tray.Core\DashboardCards.cs',
            'src\OpenBlade.Tray.Core\Properties\Resources.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.qps-ploc.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.zh-CN.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.ru-RU.resx',
            'src\OpenBlade.Setup\UninstallUserInterface.cs',
            'src\OpenBlade.Setup\Properties\Resources.resx',
            'src\OpenBlade.Setup\Properties\Resources.qps-ploc.resx',
            'src\OpenBlade.Setup\Properties\Resources.zh-CN.resx',
            'src\OpenBlade.Setup\Properties\Resources.ru-RU.resx')) {
        $source = Join-Path $repository $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $destination = Join-Path $fixture $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    return $fixture
}

function Assert-ValidatorFails {
    param(
        [Parameter(Mandatory)][scriptblock]$Mutate,
        [Parameter(Mandatory)][string]$Expected
    )

    $fixture = New-LocalizationFixture
    try {
        & $Mutate $fixture
        $failure = $null
        try { & $validator -Repository $fixture }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure -or $failure -notmatch $Expected) {
            throw "Localization validator did not reject fixture as '$Expected'. Actual: $failure"
        }
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($fixture)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unexpected localization fixture cleanup target: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Assert-ValidatorFails -Expected 'malformed' -Mutate {
    param($fixture)
    [IO.File]::WriteAllText(
        (Join-Path $fixture 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'),
        '<root><data',
        [Text.UTF8Encoding]::new($false))
}
Assert-ValidatorFails -Expected 'missing en-US resource' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'
    [xml]$document = [IO.File]::ReadAllText($path, $utf8)
    $key = [string]@($document.root.data)[0].name
    [void]$document.root.RemoveChild(@($document.root.data)[0])
    $document.Save($path)
    foreach ($culture in @('qps-ploc', 'zh-CN', 'ru-RU')) {
        $translationPath = Join-Path $fixture (
            "src\OpenBlade.Tray\Strings\$culture\Resources.resw")
        [xml]$translationDocument = [IO.File]::ReadAllText(
            $translationPath,
            $utf8)
        $translationNode = @($translationDocument.root.data |
            Where-Object name -eq $key)[0]
        [void]$translationDocument.root.RemoveChild($translationNode)
        $translationDocument.Save($translationPath)
    }
}
Assert-ValidatorFails -Expected 'missing en-US resource' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'src\OpenBlade.Tray\VariableFallbackFixture.cs'
    [IO.File]::WriteAllText(
        $path,
        @'
namespace OpenBlade.Tray;
internal static class VariableFallbackFixture
{
    public static string Get(string fallback) =>
        LocalizedResources.Get("Missing.VariableFallback", fallback);
}
'@,
        [Text.UTF8Encoding]::new($false))
}
Assert-ValidatorFails -Expected 'duplicate key' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx'
    [xml]$document = [IO.File]::ReadAllText($path, $utf8)
    [void]$document.root.AppendChild(@($document.root.data)[0].CloneNode($true))
    $document.Save($path)
}
Assert-ValidatorFails -Expected 'missing managed resource' -Mutate {
    param($fixture)
    foreach ($relative in @(
            'src\OpenBlade.Tray.Core\Properties\Resources.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.qps-ploc.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.zh-CN.resx',
            'src\OpenBlade.Tray.Core\Properties\Resources.ru-RU.resx')) {
        $path = Join-Path $fixture $relative
        [xml]$document = [IO.File]::ReadAllText($path, $utf8)
        $entry = @($document.root.data |
            Where-Object name -eq 'Device status and live readings')[0]
        [void]$document.root.RemoveChild($entry)
        $document.Save($path)
    }
}
Assert-ValidatorFails -Expected 'missing managed resource' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'src\OpenBlade.Tray.Core\ManagedVariableFallbackFixture.cs'
    [IO.File]::WriteAllText(
        $path,
        @'
namespace OpenBlade.Tray;
internal static class ManagedVariableFallbackFixture
{
    public static string Get(string fallback) =>
        TrayText.Get("Missing.Managed.VariableFallback", fallback);
}
'@,
        [Text.UTF8Encoding]::new($false))
}
Assert-ValidatorFails -Expected 'authoritative en-US value' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'src\OpenBlade.Tray.Core\ManagedLiteralFallbackFixture.cs'
    [IO.File]::WriteAllText(
        $path,
        @'
namespace OpenBlade.Tray;
internal static class ManagedLiteralFallbackFixture
{
    public static string Get() =>
        TrayText.Get("Fragment.Fans", "wrong fallback");
}
'@,
        [Text.UTF8Encoding]::new($false))
}
Assert-ValidatorFails -Expected 'invalid format string' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'build\installer\OpenBlade.Installer.UI\Properties\Resources.resx'
    [xml]$document = [IO.File]::ReadAllText($path, $utf8)
    $valueNode = @($document.root.data)[0].SelectSingleNode('value')
    $valueNode.InnerText = $valueNode.InnerText + ' {'
    $document.Save($path)
}
Assert-ValidatorFails -Expected 'changed its format placeholders' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx'
    [xml]$document = [IO.File]::ReadAllText($path, $utf8)
    $entry = @($document.root.data | Where-Object { [string]$_.value -match '\{0[^}]*\}' })[0]
    $entry.SelectSingleNode('value').InnerText =
        $entry.SelectSingleNode('value').InnerText + ' {0}'
    $document.Save($path)
}
Assert-ValidatorFails -Expected 'changed its access key' -Mutate {
    param($fixture)
    $path = Join-Path $fixture 'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx'
    [xml]$document = [IO.File]::ReadAllText($path, $utf8)
    $entry = @($document.root.data | Where-Object { [string]$_.value -match '_[\p{L}]' })[0]
    $valueNode = $entry.SelectSingleNode('value')
    $valueNode.InnerText = [regex]::Replace(
        $valueNode.InnerText,
        '_([\p{L}])',
        '$1',
        1)
    $document.Save($path)
}
Assert-ValidatorFails -Expected 'zh-CN translation key mismatch' -Mutate {
    param($fixture)
    $path = Join-Path $fixture `
        'src\OpenBlade.Tray.Core\Properties\Resources.zh-CN.resx'
    [xml]$document = [IO.File]::ReadAllText($path, $utf8)
    [void]$document.root.RemoveChild(@($document.root.data)[0])
    $document.Save($path)
}

$pseudoFixture = New-LocalizationFixture
try {
    $catalogs = @(
        @('src\OpenBlade.Tray\Strings\en-US\Resources.resw',
          'src\OpenBlade.Tray\Strings\qps-ploc\Resources.resw'),
        @('build\installer\OpenBlade.Installer.UI\Properties\Resources.resx',
          'build\installer\OpenBlade.Installer.UI\Properties\Resources.qps-ploc.resx'),
        @('src\OpenBlade.Tray.Core\Properties\Resources.resx',
          'src\OpenBlade.Tray.Core\Properties\Resources.qps-ploc.resx'),
        @('src\OpenBlade.Setup\Properties\Resources.resx',
          'src\OpenBlade.Setup\Properties\Resources.qps-ploc.resx'))
    $generatedCatalogs = @(
        $catalogs[0][0],
        $catalogs[1][0],
        $catalogs[0][1],
        $catalogs[1][1],
        $catalogs[2][1],
        $catalogs[3][1])
    $translationCatalogs = @(
        'src\OpenBlade.Tray\Strings\zh-CN\Resources.resw',
        'src\OpenBlade.Tray\Strings\ru-RU\Resources.resw',
        'build\installer\OpenBlade.Installer.UI\Properties\Resources.zh-CN.resx',
        'build\installer\OpenBlade.Installer.UI\Properties\Resources.ru-RU.resx',
        'src\OpenBlade.Tray.Core\Properties\Resources.zh-CN.resx',
        'src\OpenBlade.Tray.Core\Properties\Resources.ru-RU.resx',
        'src\OpenBlade.Setup\Properties\Resources.zh-CN.resx',
        'src\OpenBlade.Setup\Properties\Resources.ru-RU.resx')
    $expectedHashes = @{}
    foreach ($relativePath in @($generatedCatalogs) + $translationCatalogs) {
        $expectedHashes[$relativePath] = Get-FileSha256 (
            Join-Path $pseudoFixture $relativePath)
    }

    & (Join-Path $pseudoFixture 'build\Update-LocalizationResources.ps1')
    foreach ($relativePath in @($generatedCatalogs) + $translationCatalogs) {
        $actualHash = Get-FileSha256 (Join-Path $pseudoFixture $relativePath)
        if ($actualHash -cne $expectedHashes[$relativePath]) {
            throw "Regenerated catalog '$relativePath' changed bytes unexpectedly."
        }
    }
    foreach ($relativePath in @(
            'src\OpenBlade.Tray\Strings\qps-ploc\Resources.resw',
            'src\OpenBlade.Tray.Core\Properties\Resources.qps-ploc.resx')) {
        $pseudoText = [IO.File]::ReadAllText(
            (Join-Path $pseudoFixture $relativePath),
            $utf8)
        if ($pseudoText.IndexOf(
                'Ctrl+Shift+M',
                [StringComparison]::Ordinal) -lt 0) {
            throw "Pseudo-localization changed invariant shortcut syntax in '$relativePath'."
        }
    }
    & $validator -Repository $pseudoFixture
    foreach ($catalog in $catalogs) {
        [xml]$englishDocument = [IO.File]::ReadAllText(
            (Join-Path $pseudoFixture $catalog[0]),
            $utf8)
        [xml]$pseudoDocument = [IO.File]::ReadAllText(
            (Join-Path $pseudoFixture $catalog[1]),
            $utf8)
        foreach ($englishEntry in @($englishDocument.root.data)) {
            $englishValue = [string]$englishEntry.value
            if ($englishValue -match '\p{L}') { continue }
            $pseudoEntry = @($pseudoDocument.root.data |
                Where-Object name -eq $englishEntry.name)[0]
            if ([string]$pseudoEntry.value -cne $englishValue) {
                throw "Pseudo-localization changed punctuation-only resource '$($englishEntry.name)'."
            }
        }
    }

    $caseRenameXamlPath = Join-Path $pseudoFixture `
        'src\OpenBlade.Tray\MainWindow.xaml'
    $oldUid = 'MainWindow_TextBlockPerformance'
    $newUid = 'MainWindow_TextBlockPERFORMANCE'
    $xaml = [IO.File]::ReadAllText($caseRenameXamlPath, $utf8)
    $updatedXaml = $xaml.Replace(
        "x:Uid=`"$oldUid`"",
        "x:Uid=`"$newUid`"")
    if ($updatedXaml -ceq $xaml) {
        throw "Case-only x:Uid fixture could not find '$oldUid'."
    }
    [IO.File]::WriteAllText(
        $caseRenameXamlPath,
        $updatedXaml,
        [Text.UTF8Encoding]::new($false))
    & (Join-Path $pseudoFixture 'build\Extract-WinUiLocalization.ps1')

    [xml]$renamedCatalog = [IO.File]::ReadAllText(
        (Join-Path $pseudoFixture 'src\OpenBlade.Tray\Strings\en-US\Resources.resw'),
        $utf8)
    $oldKey = "$oldUid.Text"
    $newKey = "$newUid.Text"
    $oldEntries = @($renamedCatalog.root.data |
        Where-Object { [string]$_.name -ceq $oldKey })
    $newEntries = @($renamedCatalog.root.data |
        Where-Object { [string]$_.name -ceq $newKey })
    if ($oldEntries.Count -ne 0 -or $newEntries.Count -ne 1) {
        throw 'Case-only x:Uid rename did not replace the preserved resource key casing.'
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($pseudoFixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected localization fixture cleanup target: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Write-Host 'Localization resource tests passed.'
