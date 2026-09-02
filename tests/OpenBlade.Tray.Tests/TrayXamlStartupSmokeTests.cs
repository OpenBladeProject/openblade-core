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
    public async Task LanguageChangesReloadResourcesWithoutRestartingTheProcess()
    {
        foreach (bool existingPreferences in new[] { false, true })
        {
            await RunTraySmokeAsync(
                "--live-localization-smoke",
                "OpenBlade-live-localization-smoke",
                localizationLanguage: "en-US",
                isolatePreferences: true,
                seedExistingPreferences: existingPreferences);
        }
    }

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
        string? localizationLanguage = null,
        bool isolatePreferences = false,
        bool seedExistingPreferences = false)
    {
        string executable = GetTrayExecutablePath(pseudoLocalized);
        Assert.True(File.Exists(executable), $"Tray executable not found: {executable}");
        string reportPath = Path.Combine(
            Path.GetTempPath(),
            $"{reportPrefix}-{Guid.NewGuid():N}.txt");
        string? isolatedPreferenceDirectory = isolatePreferences
            ? Path.Combine(
                Path.GetTempPath(),
                $"OpenBlade-live-localization-profile-{Guid.NewGuid():N}")
            : null;
        string? isolatedPreferencePath = isolatedPreferenceDirectory is null
            ? null
            : Path.Combine(isolatedPreferenceDirectory, "preferences.json");
        Process? process = null;
        try
        {
            if (seedExistingPreferences)
            {
                Assert.NotNull(isolatedPreferencePath);
                await new UiPreferenceStore(isolatedPreferencePath).SaveAsync(
                    UiPreferences.Default with
                    {
                        SpecialKeyBindings = SpecialKeyBindingDefaults.Bindings
                            .Select(binding => binding.Slot == SpecialKeySlot.M1
                                ? new SpecialKeyBindingPreference(
                                    SpecialKeySlot.M1,
                                    new SpecialKeyAction(
                                        SpecialKeyActionKind.KeyboardShortcut,
                                        "Ctrl+Shift+M"))
                                : binding)
                            .ToArray(),
                        UpdateCheckInterval = UpdateCheckInterval.Daily,
                        UpdateCheckPromptShown = true,
                    });
            }

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
            if (isolatedPreferencePath is not null)
            {
                startInfo.Environment["OPENBLADE_LIVE_LOCALIZATION_SMOKE_PREFERENCE_PATH"] =
                    isolatedPreferencePath;
            }

            process = Process.Start(startInfo);
            Assert.NotNull(process);
            TimeSpan processTimeout = isolatePreferences
                ? TimeSpan.FromSeconds(45)
                : TimeSpan.FromSeconds(30);
            using var timeout = new CancellationTokenSource(processTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception)
            {
                string timeoutDiagnostic = await ReadSmokeReportAsync(reportPath);
                await StopSmokeProcessAsync(process, argument, timeoutDiagnostic);

                throw new TimeoutException(
                    $"Tray smoke '{argument}' did not exit. "
                    + $"Phase report:{Environment.NewLine}{timeoutDiagnostic}",
                    exception);
            }

            string diagnostic = await ReadSmokeReportAsync(reportPath);
            string exitCode = process.ExitCode == 0
                ? "0"
                : $"{process.ExitCode} (0x{unchecked((uint)process.ExitCode):X8})";
            Assert.True(
                process.ExitCode == 0,
                $"Tray smoke '{argument}' process {process.Id} exited with "
                    + $"{exitCode}:{Environment.NewLine}{diagnostic}");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    string cleanupDiagnostic = await ReadSmokeReportAsync(reportPath);
                    await StopSmokeProcessAsync(process, argument, cleanupDiagnostic);
                }
                process.Dispose();
            }
            File.Delete(reportPath);
            if (isolatedPreferenceDirectory is not null
                && Directory.Exists(isolatedPreferenceDirectory))
            {
                Directory.Delete(isolatedPreferenceDirectory, recursive: true);
            }
        }
    }

    private static async Task StopSmokeProcessAsync(
        Process process,
        string argument,
        string diagnostic)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(cleanupTimeout.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException(
                $"Tray smoke '{argument}' did not exit after kill. "
                + $"Phase report:{Environment.NewLine}{diagnostic}",
                exception);
        }
    }

    private static async Task<string> ReadSmokeReportAsync(string reportPath) =>
        File.Exists(reportPath)
            ? await File.ReadAllTextAsync(reportPath)
            : "<no smoke report was written>";

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
