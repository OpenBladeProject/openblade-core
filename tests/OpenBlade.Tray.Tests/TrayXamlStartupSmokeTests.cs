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
    public Task ResponsiveChoiceSelectorPreservesAuthoritativeSelection() =>
        RunTraySmokeAsync(
            "--responsive-choice-selector-smoke",
            "OpenBlade-responsive-selector-smoke");

    private static async Task RunTraySmokeAsync(string argument, string reportPrefix)
    {
        string executable = GetTrayExecutablePath();
        Assert.True(File.Exists(executable), $"Tray executable not found: {executable}");
        string reportPath = Path.Combine(
            Path.GetTempPath(),
            $"{reportPrefix}-{Guid.NewGuid():N}.txt");

        using var process = Process.Start(new ProcessStartInfo(
            executable,
            $"{argument} \"{reportPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        });
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

    private static string GetTrayExecutablePath([CallerFilePath] string testPath = "")
    {
        string configuration = typeof(TrayXamlStartupSmokeTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? "Debug";
        string repository = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            ".."));
        return Path.Combine(
            repository,
            "src",
            "OpenBlade.Tray",
            "bin",
            configuration,
            "net10.0-windows10.0.26100.0",
            "win-x64",
            "OpenBlade.Tray.exe");
    }
}
