using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

[Collection(InteractiveDesktopTestCollection.Name)]
public sealed class TrayXamlStartupSmokeTests
{
    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task GpuTuningStartupRestoresNonzeroSlidersInMainAndQuickControls() =>
        RunTraySmokeAsync("--gpu-startup-smoke", "OpenBlade-gpu-startup-smoke");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task LiveCardsAndMenuReorderWithoutCustomizationMode() =>
        RunTraySmokeAsync("--live-reorder-smoke", "OpenBlade-live-reorder-smoke");

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
    public Task LaptopPerformanceRowMatchesFansAndExplainsHyperBoostRestrictions() =>
        RunTraySmokeAsync(
            "--performance-row-layout-smoke",
            "OpenBlade-performance-row-layout-smoke");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task ResponsivePageHeaderStabilizesAndWrapsSaveAndRestoreActions() =>
        RunTraySmokeAsync(
            "--responsive-page-header-smoke",
            "OpenBlade-responsive-page-header-smoke");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task NativeCaptionsFollowResolvedWindowTheme() =>
        RunTraySmokeAsync("--caption-theme-smoke", "OpenBlade-caption-theme-smoke");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task TelemetryNotificationsRenderOnceAndKeepNoSampleTimeProgression() =>
        RunTraySmokeAsync("--performance-audit", "OpenBlade-telemetry-performance");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task ResourceWorkloadStaysDisconnectedAndRendersAcceptedSamples() =>
        RunTraySmokeAsync("--resource-performance-audit", "OpenBlade-resource-performance", localizationLanguage: "en-US");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task OffscreenResourceWorkloadStaysNonactivatingAndReclaimsWindows() =>
        RunTraySmokeAsync("--resource-performance-audit", "OpenBlade-offscreen-resource-performance",
            localizationLanguage: "en-US", offscreenResourceWindows: true);

    private static async Task RunTraySmokeAsync(
        string argument,
        string reportPrefix,
        bool pseudoLocalized = false,
        string? localizationLanguage = null,
        bool isolatePreferences = false,
        bool seedExistingPreferences = false,
        bool offscreenResourceWindows = false)
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
        using var desktop = new OpenBlade.Testing.WindowsVisualTestDesktop();
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
            if (argument == "--resource-performance-audit")
            {
                startInfo.Environment["OPENBLADE_RESOURCE_PHASE_SECONDS"] = "2";
                startInfo.Environment["OPENBLADE_RESOURCE_WINDOW_KIND"] = "dashboard";
                startInfo.Environment["OPENBLADE_RESOURCE_WINDOW_DWELL_MS"] = "0";
                startInfo.Environment["OPENBLADE_RESOURCE_OBSERVE_RENDERING"] = "0";
                startInfo.Environment["OPENBLADE_RESOURCE_OFFSCREEN"] = offscreenResourceWindows ? "1" : "0";
                startInfo.Environment["OPENBLADE_RESOURCE_INPUT_DESKTOP"] = "0";
                startInfo.Environment["OPENBLADE_RESOURCE_EXPECTED_DESKTOP"] = desktop.Name;
            }

            process = desktop.Start(startInfo);
            Assert.NotNull(process);
            // Reordering captures both section and nested-card states; its additional
            // frames need a bounded render budget under full-solution test load.
            TimeSpan processTimeout = argument is "--live-reorder-smoke" or "--resource-performance-audit"
                ? TimeSpan.FromSeconds(60)
                : isolatePreferences ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(30);
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
            if (argument == "--performance-audit")
            {
                using var report = System.Text.Json.JsonDocument.Parse(diagnostic);
                Assert.True(report.RootElement.GetProperty("disconnected").GetBoolean());
                Assert.True(report.RootElement.GetProperty("observationChecksPassed").GetBoolean());
                Assert.True(report.RootElement.GetProperty("replayActionChecksPassed").GetBoolean());
                Assert.True(report.RootElement.GetProperty("gapChartChecksPassed").GetBoolean());
                Assert.True(new FileInfo(Path.ChangeExtension(reportPath, ".png")).Length > 0);
                Assert.True(new FileInfo(Path.ChangeExtension(reportPath, ".gaps.png")).Length > 0);
            }
            if (argument == "--resource-performance-audit")
            {
                using var report = System.Text.Json.JsonDocument.Parse(diagnostic);
                var root = report.RootElement;
                Assert.True(root.GetProperty("disconnected").GetBoolean());
                Assert.Equal(0, root.GetProperty("serviceCalls").GetInt32());
                Assert.Equal(offscreenResourceWindows, root.GetProperty("offscreenWindows").GetBoolean());
                Assert.False(root.GetProperty("requireInputDesktop").GetBoolean());
                Assert.Equal(2, root.GetProperty("phases").GetArrayLength());
                int operations = root.GetProperty("activityOperations").GetInt32();
                Assert.True(operations > 0);
                Assert.Equal(operations, root.GetProperty("activitySampleChartRebuilds").GetInt32());
                Assert.True(root.GetProperty("dashboardCycles").GetInt32() >= 5);
                Assert.Equal(0, root.GetProperty("dashboardWrappersAliveAfterCollection").GetInt32());
                Assert.Equal(0, root.GetProperty("dashboardContentsAliveAfterCollection").GetInt32());
                Assert.Equal(0, root.GetProperty("dashboardNativeWindowsSurvivedClose").GetInt32());
                Assert.Equal(0, root.GetProperty("dashboardRevealHosts").GetInt32());
                Assert.False(root.GetProperty("retiredMainWindowAliveAfterCollection").GetBoolean());
                Assert.False(root.GetProperty("retiredMainContentAliveAfterCollection").GetBoolean());
                Assert.False(root.GetProperty("retiredMainNativeWindowSurvivedClose").GetBoolean());
                Assert.True(root.GetProperty("bindingCleanupFailureChecksPassed").GetBoolean());
                Assert.True(root.GetProperty("nativeTitleBarCleanupChecksPassed").GetBoolean());
                if (offscreenResourceWindows)
                {
                    var presentation = root.GetProperty("windowPresentation");
                    int cycles = root.GetProperty("dashboardCycles").GetInt32();
                    Assert.Equal(cycles + 2, presentation.GetProperty("showCount").GetInt32());
                    // Before/after show and after layout for every window, plus
                    // owner checks after each dashboard disposal and after activity.
                    Assert.True(presentation.GetProperty("verificationCount").GetInt32() >= 3 * (cycles + 2) + cycles + 1);
                    var desktopIdentity = presentation.GetProperty("desktop");
                    Assert.Equal(desktop.Name, desktopIdentity.GetProperty("currentThreadDesktop").GetString());
                    Assert.NotEqual(System.Text.Json.JsonValueKind.True, desktopIdentity.GetProperty("matchesInputDesktop").ValueKind);
                }
            }
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
            if (argument == "--resource-performance-audit")
            {
                foreach (string suffix in new[] { ".ready", ".phase", ".preferences.json", ".retired.preferences.json", ".error.txt" })
                {
                    File.Delete(reportPath + suffix);
                }
            }
            if (argument == "--performance-audit")
            {
                File.Delete(reportPath + ".error.txt");
                File.Delete(reportPath + ".replay.csv");
                File.Delete(Path.ChangeExtension(reportPath, ".png"));
                File.Delete(Path.ChangeExtension(reportPath, ".gaps.png"));
            }
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
            : File.Exists(reportPath + ".error.txt")
                ? await File.ReadAllTextAsync(reportPath + ".error.txt")
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
        // Smoke arguments exist only in harness builds; the ordinary bin\ output
        // is the shipped Tray and ignores them.
        string outputDirectory = pseudoLocalized
            ? "bin-localization-preview"
            : "bin-developer-harness";
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
