using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

[Collection(InteractiveDesktopTestCollection.Name)]
public sealed class TrayXamlStartupSmokeTests
{
    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task DashboardCompiledXamlLoadsWithEveryDefinitionVisible() =>
        RunTraySmokeAsync("--xaml-startup-smoke", "OpenBlade-xaml-smoke");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task PseudoLocalizedDashboardCompiledXamlLoads() =>
        RunTraySmokeAsync(
            "--xaml-startup-smoke",
            "OpenBlade-xaml-pseudo-smoke",
            pseudoLocalized: true);

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task UnsupportedLanguageFallsBackToEnglishAndCompiledXamlLoads() =>
        RunTraySmokeAsync(
            "--xaml-startup-smoke",
            "OpenBlade-xaml-fallback-smoke",
            localizationLanguage: "de-DE");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task SimplifiedChineseDashboardCompiledXamlLoads() =>
        RunTraySmokeAsync(
            "--xaml-startup-smoke",
            "OpenBlade-xaml-zh-CN-smoke",
            localizationLanguage: "zh-CN");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task RussianDashboardCompiledXamlLoads() =>
        RunTraySmokeAsync(
            "--xaml-startup-smoke",
            "OpenBlade-xaml-ru-RU-smoke",
            localizationLanguage: "ru-RU");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task ResponsiveChoiceSelectorPreservesAuthoritativeSelection() =>
        RunTraySmokeAsync(
            "--responsive-choice-selector-smoke",
            "OpenBlade-responsive-selector-smoke");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task ResponsivePageHeaderStabilizesAndWrapsSaveAndRestoreActions() =>
        RunTraySmokeAsync(
            "--responsive-page-header-smoke",
            "OpenBlade-responsive-page-header-smoke");

    private static async Task RunTraySmokeAsync(
        string argument,
        string reportPrefix,
        bool pseudoLocalized = false,
        string? localizationLanguage = null)
    {
        string executable = GetTrayExecutablePath(pseudoLocalized);
        Assert.True(File.Exists(executable), $"Tray executable not found: {executable}");
        string reportPath = Path.Combine(
            Path.GetTempPath(),
            $"{reportPrefix}-{Guid.NewGuid():N}.txt");

        var startInfo = new ProcessStartInfo(
            executable,
            $"{argument} \"{reportPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        if (pseudoLocalized)
        {
            startInfo.Environment["OPENBLADE_PSEUDO_LOCALIZATION"] = "1";
        }
        if (!string.IsNullOrWhiteSpace(localizationLanguage))
        {
            startInfo.Environment["OPENBLADE_LOCALIZATION_LANGUAGE"] =
                localizationLanguage;
        }
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The tray XAML startup smoke test did not exit.");
        }

        string diagnostic = File.Exists(reportPath)
            ? await File.ReadAllTextAsync(reportPath)
            : string.Empty;
        File.Delete(reportPath);
        Assert.True(
            process.ExitCode == 0,
            $"Tray smoke '{argument}' exited with {process.ExitCode}: {diagnostic}");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", false)]
    public void InteractiveDesktopSkipRequiresExplicitOptOut(
        string? value,
        bool expected)
    {
        Assert.Equal(expected, InteractiveDesktopTestPolicy.ShouldSkip(value));
    }

    [Fact]
    public void ExecutableSmokeDisablesInAssemblyParallelization()
    {
        Assert.IsType<CollectionAttribute>(
            typeof(TrayXamlStartupSmokeTests)
                .GetCustomAttribute<CollectionAttribute>());

        CollectionDefinitionAttribute definition =
            Assert.IsType<CollectionDefinitionAttribute>(
                typeof(InteractiveDesktopTestCollection)
                    .GetCustomAttribute<CollectionDefinitionAttribute>());
        Assert.True(definition.DisableParallelization);
    }

    private static string GetTrayExecutablePath(
        bool pseudoLocalized,
        [CallerFilePath] string testPath = "")
    {
        string configuration = typeof(TrayXamlStartupSmokeTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? "Debug";
        string repository = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            ".."));
        string outputDirectory = pseudoLocalized
            ? "bin-localization-preview"
            : "bin";
        return Path.Combine(
            repository,
            "src",
            "OpenBlade.Tray",
            outputDirectory,
            configuration,
            "net10.0-windows10.0.26100.0",
            "win-x64",
            pseudoLocalized
                ? "OpenBlade.LocalizationPreview.exe"
                : "OpenBlade.Tray.exe");
    }
}
