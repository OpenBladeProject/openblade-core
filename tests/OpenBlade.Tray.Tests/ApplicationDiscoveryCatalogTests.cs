using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ApplicationDiscoveryCatalogTests
{
    [Fact]
    public void TryReadEpicManifest_RequiresStringPropertiesAndSkipsMalformedShapes()
    {
        const string valid = """
            {
              "InstallLocation": "C:\\Games\\Example",
              "LaunchExecutable": "ExampleLauncher.exe",
              "DisplayName": "Example"
            }
            """;

        Assert.True(ApplicationDiscoveryCatalog.TryReadEpicManifest(
            valid, out EpicApplicationManifest? manifest));
        Assert.Equal(@"C:\Games\Example", manifest?.InstallRoot);
        Assert.Equal("ExampleLauncher.exe", manifest?.DeclaredExecutable);
        Assert.Equal("Example", manifest?.DisplayName);

        Assert.False(ApplicationDiscoveryCatalog.TryReadEpicManifest(
            """{"InstallLocation":42,"LaunchExecutable":"Game.exe","DisplayName":"Game"}""",
            out _));
        Assert.False(ApplicationDiscoveryCatalog.TryReadEpicManifest("{", out _));
    }

    [Fact]
    public void SelectEpicExecutableVariants_RetainsDeclaredBootstrapAndReviewedForegroundVariant()
    {
        const string root = @"C:\Games\Example";
        string declared = Path.Combine(root, "ExampleLauncher.exe");
        string foreground = Path.Combine(
            root, "Example", "Binaries", "Win64", "Example-Win64-Shipping.exe");
        string helper = Path.Combine(root, "Support", "ExampleCrashReporter.exe");

        IReadOnlyList<string> selected = ApplicationDiscoveryCatalog.SelectEpicExecutableVariants(
            root,
            "Example",
            declared,
            [declared, foreground, helper]);

        Assert.Contains(declared, selected);
        Assert.Contains(foreground, selected);
        Assert.DoesNotContain(helper, selected);
        Assert.True(selected.Count <= 4);
    }

    [Fact]
    public void SelectExecutableVariants_RemovesHelpersAndRetainsMeaningfulVariants()
    {
        const string root = @"C:\Games\Example";
        string[] paths =
        [
            Path.Combine(root, "Example.exe"),
            Path.Combine(root, "Binaries", "Win64", "Example-Win64-Shipping.exe"),
            Path.Combine(root, "ExampleDX11.exe"),
            Path.Combine(root, "ExampleDX12.exe"),
            Path.Combine(root, "ExampleLauncher.exe"),
            Path.Combine(root, "Support", "ExampleCrashReporter.exe"),
            Path.Combine(root, "uninstall.exe"),
        ];

        IReadOnlyList<string> selected = ApplicationDiscoveryCatalog.SelectExecutableVariants(
            root, "Example", paths);

        Assert.Contains(Path.Combine(root, "Example.exe"), selected);
        Assert.Contains(Path.Combine(root, "ExampleDX11.exe"), selected);
        Assert.Contains(Path.Combine(root, "ExampleDX12.exe"), selected);
        Assert.Contains(Path.Combine(root, "Binaries", "Win64", "Example-Win64-Shipping.exe"), selected);
        Assert.DoesNotContain(selected, path => path.Contains("CrashReporter", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selected, path => path.Contains("Launcher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selected, path => path.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectExecutableVariants_RetainsUnrealBootstrapAndForegroundExecutable()
    {
        const string root = @"C:\Games\UnrealExample";
        string bootstrap = Path.Combine(root, "UnrealExample.exe");
        string foreground = Path.Combine(
            root, "UnrealExample", "Binaries", "Win64", "UnrealExample-Win64-Shipping.exe");

        IReadOnlyList<string> selected = ApplicationDiscoveryCatalog.SelectExecutableVariants(
            root,
            "Unreal Example",
            [bootstrap, foreground, foreground]);

        Assert.Equal(2, selected.Count);
        Assert.Contains(bootstrap, selected);
        Assert.Contains(foreground, selected);
    }

    [Fact]
    public void SelectExecutableVariants_RetainsLauncherWhenItIsTheOnlyGameEntryPoint()
    {
        const string root = @"C:\Games\LauncherOnly";
        string launcher = Path.Combine(root, "LauncherOnlyLauncher.exe");

        string selected = Assert.Single(ApplicationDiscoveryCatalog.SelectExecutableVariants(
            root, "Launcher Only", [launcher]));

        Assert.Equal(launcher, selected);
    }

    [Fact]
    public void Group_CombinesExecutableVariantsByInstalledTitleAndRoot()
    {
        InstalledApplicationCandidate[] candidates =
        [
            new("Example", @"C:\Games\Example\ExampleDX11.exe", "Steam", @"C:\Games\Example"),
            new("Example", @"C:\Games\Example\ExampleDX11.exe", "Steam", @"C:\Games\Example"),
            new("example", @"C:\Games\Example\ExampleDX12.exe", "Steam", @"c:\games\example"),
            new("Example", @"D:\Games\Example\Example.exe", "Epic Games", @"D:\Games\Example"),
        ];

        IReadOnlyList<InstalledApplicationGroup> groups = ApplicationDiscoveryCatalog.Group(candidates, searchText: null);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Single(group => group.InstallRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase)).Executables.Count);
        Assert.Single(groups.Single(group => group.InstallRoot.StartsWith("D:", StringComparison.OrdinalIgnoreCase)).Executables);
    }

    [Fact]
    public void Group_SearchesExecutableNameAndPathWithoutChangingExactPath()
    {
        const string exactPath = @"C:\Games\Example\Binaries\ExampleDX12.exe";
        InstalledApplicationCandidate[] candidates =
        [
            new("Example", exactPath, "Steam", @"C:\Games\Example"),
            new("Other", @"D:\Games\Other\Other.exe", "Xbox", @"D:\Games\Other"),
        ];

        InstalledApplicationGroup group = Assert.Single(
            ApplicationDiscoveryCatalog.Group(candidates, "dx12"));

        Assert.Equal(exactPath, Assert.Single(group.Executables).ExecutablePath);
    }
}
