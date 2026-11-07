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

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task LanguageChangesReuseNativeShellsAndReleaseRetiredContent() =>
        RunTraySmokeAsync("--resource-performance-audit", "OpenBlade-resource-localization",
            localizationLanguage: "en-US", offscreenResourceWindows: true, resourceWindowKind: "localized-content");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task NativeWindowLimitsPreserveSizingAndReleaseTheirShellCallback() =>
        RunTraySmokeAsync("--window-size-smoke", "OpenBlade-window-size");

    [InteractiveDesktopFact]
    [Trait("Category", "InteractiveDesktop")]
    public Task NativeCloseHandlingPreservesHideAndTerminalCleanup() =>
        RunTraySmokeAsync("--window-close-smoke", "OpenBlade-window-close");

    private static async Task RunTraySmokeAsync(
        string argument,
        string reportPrefix,
        bool pseudoLocalized = false,
        string? localizationLanguage = null,
        bool isolatePreferences = false,
        bool seedExistingPreferences = false,
        bool offscreenResourceWindows = false,
        string resourceWindowKind = "dashboard")
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
            if (argument is "--resource-performance-audit" or "--window-size-smoke" or "--window-close-smoke")
            {
                startInfo.Environment["OPENBLADE_RESOURCE_PHASE_SECONDS"] = "2";
                startInfo.Environment["OPENBLADE_RESOURCE_WINDOW_KIND"] = resourceWindowKind;
                startInfo.Environment["OPENBLADE_RESOURCE_WINDOW_DWELL_MS"] = "0";
                startInfo.Environment["OPENBLADE_RESOURCE_OBSERVE_RENDERING"] = "0";
                startInfo.Environment["OPENBLADE_RESOURCE_OBSERVE_RETIREMENT"] = offscreenResourceWindows && resourceWindowKind == "dashboard" ? "1" : "0";
                startInfo.Environment["OPENBLADE_RESOURCE_OFFSCREEN"] = offscreenResourceWindows ? "1" : "0";
                startInfo.Environment["OPENBLADE_RESOURCE_INPUT_DESKTOP"] = "0";
                startInfo.Environment["OPENBLADE_RESOURCE_EXPECTED_DESKTOP"] = desktop.Name;
            }

            process = desktop.Start(startInfo);
            Assert.NotNull(process);
            // Reordering captures both section and nested-card states; its additional
            // frames need a bounded render budget under full-solution test load.
            // Retirement observation adds twelve seconds plus collection work.
            TimeSpan processTimeout = argument is "--live-reorder-smoke" or "--resource-performance-audit"
                ? TimeSpan.FromSeconds(offscreenResourceWindows ? 75 : 60)
                : isolatePreferences ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(30);
            using var timeout = new CancellationTokenSource(processTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception)
            {
                string timeoutDiagnostic = await ReadSmokeReportAsync(reportPath, includeProgress: true);
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
            if (argument == "--resource-performance-audit" && resourceWindowKind == "localized-content")
            {
                using var report = System.Text.Json.JsonDocument.Parse(diagnostic);
                var root = report.RootElement;
                Assert.True(root.GetProperty("disconnected").GetBoolean());
                Assert.Equal(0, root.GetProperty("serviceCalls").GetInt32());
                Assert.True(root.GetProperty("stableNativeShells").GetBoolean());
                Assert.True(root.GetProperty("attachmentRollbackPassed").GetBoolean());
                Assert.True(root.GetProperty("shellMessageHostsPreserved").GetBoolean());
                Assert.True(root.GetProperty("languageChanges").GetInt32() >= 12);
                Assert.Equal(0, root.GetProperty("retiredReferencesAlive").GetInt32());
                Assert.Equal(3, root.GetProperty("checkpoints").GetArrayLength());
                var checkpoints = root.GetProperty("checkpoints");
                foreach (string type in new[] { "Composition", "Timer" })
                {
                    string index = root.GetProperty("nativeHandleTypeNames").GetProperty("names")
                        .EnumerateObject().Single(property => property.Value.GetString() == type).Name;
                    int baseline = checkpoints[0].GetProperty("nativeHandles").GetProperty("typeCounts").GetProperty(index).GetInt32();
                    int retired = checkpoints[2].GetProperty("nativeHandles").GetProperty("typeCounts").GetProperty(index).GetInt32();
                    Assert.Equal(baseline, retired);
                }
                Assert.Equal(desktop.Name, root.GetProperty("windowPresentation").GetProperty("desktop")
                    .GetProperty("currentThreadDesktop").GetString());
            }
            if (argument == "--resource-performance-audit" && resourceWindowKind != "localized-content")
            {
                using var report = System.Text.Json.JsonDocument.Parse(diagnostic);
                var root = report.RootElement;
                Assert.True(root.GetProperty("disconnected").GetBoolean());
                Assert.Equal(0, root.GetProperty("serviceCalls").GetInt32());
                Assert.Equal(offscreenResourceWindows, root.GetProperty("offscreenWindows").GetBoolean());
                Assert.Equal(offscreenResourceWindows, root.GetProperty("observeRetirement").GetBoolean());
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
                    var checkpoints = root.GetProperty("retirementCheckpoints").EnumerateArray().ToArray();
                    var expectedStages = new[] { "after-warmup-collection" }
                        .Concat(Enumerable.Repeat("after-window-close", cycles - 4))
                        .Concat(["after-activity-collection", "drain-1", "drain-2", "drain-3", "drain-4", "after-retirement-idle"]);
                    Assert.Equal(expectedStages, checkpoints.Select(checkpoint => checkpoint.GetProperty("stage").GetString()));
                    double previousElapsed = -1;
                    for (int index = 0; index < checkpoints.Length; index++)
                    {
                        var checkpoint = checkpoints[index];
                        Assert.Equal(Math.Min(4 + index, cycles), checkpoint.GetProperty("dashboardCycles").GetInt32());
                        double elapsed = checkpoint.GetProperty("elapsedSeconds").GetDouble();
                        Assert.True(elapsed >= previousElapsed);
                        previousElapsed = elapsed;
                        Assert.True(checkpoint.GetProperty("managedBytesEstimate").GetInt64() > 0);
                        Assert.True(checkpoint.GetProperty("lastGcCommittedBytes").GetInt64() > 0);
                        Assert.True(checkpoint.GetProperty("lastGcIndex").GetInt64() > 0);
                        Assert.InRange(checkpoint.GetProperty("lastGcGeneration").GetInt32(), 0, GC.MaxGeneration);
                        var handles = checkpoint.GetProperty("nativeHandles");
                        if (handles.GetProperty("available").GetBoolean())
                        {
                            Assert.True(handles.GetProperty("handleCount").GetInt32() > 0);
                        }
                        else
                        {
                            Assert.Equal(System.Text.Json.JsonValueKind.Null, handles.GetProperty("handleCount").ValueKind);
                            Assert.False(string.IsNullOrWhiteSpace(handles.GetProperty("unavailableReason").GetString()));
                        }
                    }
                    Assert.Equal(cycles + 2, presentation.GetProperty("showCount").GetInt32());
                    // Before/after show and after layout for every window, plus
                    // owner checks after each dashboard disposal and after activity.
                    Assert.True(presentation.GetProperty("verificationCount").GetInt32() >= 3 * (cycles + 2) + cycles + 1);
                    var desktopIdentity = presentation.GetProperty("desktop");
                    Assert.Equal(desktop.Name, desktopIdentity.GetProperty("currentThreadDesktop").GetString());
                    Assert.NotEqual(System.Text.Json.JsonValueKind.True, desktopIdentity.GetProperty("matchesInputDesktop").ValueKind);
                }
                else
                {
                    Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("retirementCheckpoints").ValueKind);
                }
            }
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    string cleanupDiagnostic = await ReadSmokeReportAsync(reportPath, includeProgress: true);
                    await StopSmokeProcessAsync(process, argument, cleanupDiagnostic);
                }
                process.Dispose();
            }
            File.Delete(reportPath);
            if (argument == "--resource-performance-audit")
            {
                foreach (string suffix in new[] { ".ready", ".phase", ".phases.txt", ".retention.json", ".preferences.json", ".retired.preferences.json", ".error.txt" })
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

    private static async Task<string> ReadSmokeReportAsync(string reportPath, bool includeProgress = false)
    {
        string diagnostic;
        try
        {
            diagnostic = File.Exists(reportPath)
                ? await File.ReadAllTextAsync(reportPath)
                : File.Exists(reportPath + ".error.txt")
                    ? await File.ReadAllTextAsync(reportPath + ".error.txt")
                    : "<no smoke report was written>";
        }
        catch (Exception exception) when (includeProgress && exception is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"<smoke report read failed: {exception.GetType().Name}>";
        }
        if (!includeProgress) { return diagnostic; }

        // The resource audit writes its final JSON only after disposal. Preserve
        // existing progress in timeout/TRX output before finally deletes these
        // sidecars; a missing final report alone does not locate the stalled step.
        var sections = new List<string> { diagnostic };
        foreach (string suffix in new[] { ".ready", ".phase", ".phases.txt", ".retention.json" })
        {
            string path = reportPath + suffix;
            try
            {
                if (!File.Exists(path)) { continue; }
                string text = await File.ReadAllTextAsync(path);
                const int MaximumProgressCharacters = 16 * 1024;
                if (text.Length > MaximumProgressCharacters)
                { text = "<earlier progress omitted>" + text[^MaximumProgressCharacters..]; }
                sections.Add($"{Path.GetFileName(path)}:{Environment.NewLine}{text}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { sections.Add($"{Path.GetFileName(path)}: <progress read failed: {exception.GetType().Name}>"); }
        }
        return string.Join(Environment.NewLine, sections);
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
