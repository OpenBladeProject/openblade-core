namespace OpenBlade.Tray.Tests;

public sealed class CrashReportingContractTests
{
    [Fact]
    public void TrayRegistersProcessWideCrashReportingWithoutSwallowingFailures()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");

        Assert.Contains("UnhandledException += OnXamlUnhandledException", app, StringComparison.Ordinal);
        Assert.Contains(
            "AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "TaskScheduler.UnobservedTaskException += OnUnobservedTaskException",
            app,
            StringComparison.Ordinal);
        Assert.Contains("RegisterCrashReporting(componentName);", app, StringComparison.Ordinal);
        Assert.Contains("OpenBlade.Tray.StartHelper", app, StringComparison.Ordinal);
        Assert.Contains("OpenBlade.Tray.ShutdownHelper", app, StringComparison.Ordinal);
        Assert.Contains("OpenBlade.Tray.StopRazerServicesHelper", app, StringComparison.Ordinal);
        Assert.Contains("CrashReportWriter.CreateDefault", app, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref fatalCrashReported, 0)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Handled = true", app, StringComparison.Ordinal);
        Assert.DoesNotContain("args.SetObserved()", app, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.False(string.IsNullOrWhiteSpace(directory));
        return File.ReadAllText(Path.Combine([directory!, .. parts]));
    }
}
