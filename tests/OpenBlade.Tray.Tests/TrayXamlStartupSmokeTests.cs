using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class TrayXamlStartupSmokeTests
{
    [Fact]
    public async Task DashboardCompiledXamlLoadsWithEveryDefinitionVisible()
    {
        string executable = GetTrayExecutablePath();
        Assert.True(File.Exists(executable), $"Tray executable not found: {executable}");
        string reportPath = Path.Combine(
            Path.GetTempPath(),
            $"OpenBlade-xaml-smoke-{Guid.NewGuid():N}.txt");

        using var process = Process.Start(new ProcessStartInfo(
            executable,
            $"--xaml-startup-smoke \"{reportPath}\"")
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
            $"Tray XAML startup smoke exited with {process.ExitCode}: {diagnostic}");
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
