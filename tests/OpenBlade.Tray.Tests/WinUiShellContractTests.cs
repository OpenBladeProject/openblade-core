using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class WinUiShellContractTests
{
    [Fact]
    public void TrayExecutableIsPinnedUnpackagedSelfContainedWinUiAndContainsNoWpf()
    {
        string project = ReadRepositoryFile("src", "OpenBlade.Tray", "OpenBlade.Tray.csproj");
        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<SupportedOSPlatformVersion>10.0.26100.0</SupportedOSPlatformVersion>", project, StringComparison.Ordinal);
        Assert.Contains("<UseWinUI>true</UseWinUI>", project, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>true</SelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project, StringComparison.Ordinal);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", project, StringComparison.Ordinal);
        Assert.Contains("<Platforms>x64</Platforms>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishTrimmed>false</PublishTrimmed>", project, StringComparison.Ordinal);
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"Microsoft.WindowsAppSDK\" Version=\"2.2.0\"", project, StringComparison.Ordinal);
        Assert.Contains("<ImportFrameworkWinFXTargets>true</ImportFrameworkWinFXTargets>", project, StringComparison.Ordinal);
        Assert.Contains("prevents the Windows Desktop SDK from sending WinUI XAML through the WPF compiler", project, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWPF", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationCore", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.WindowsDesktop.App.WPF", project, StringComparison.OrdinalIgnoreCase);

        string manifest = ReadRepositoryFile("src", "OpenBlade.Tray", "app.manifest");
        Assert.Contains("<supportedOS Id=\"{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}\" />", manifest, StringComparison.Ordinal);
        Assert.Contains("<maxversiontested Id=\"10.0.26100.0\" />", manifest, StringComparison.Ordinal);
        Assert.Contains(">PerMonitorV2</dpiAwareness>", manifest, StringComparison.Ordinal);
        Assert.Contains(">true</longPathAware>", manifest, StringComparison.Ordinal);

        string trayRoot = Path.GetDirectoryName(ProjectPath)!;
        foreach (string file in Directory.EnumerateFiles(trayRoot, "*", SearchOption.AllDirectories)
                     .Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml"))
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DynamicResource", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Wpf", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WinUiSpecificAdaptersPreserveDispatchAndNotificationAreaContracts()
    {
        string dispatcher = ReadRepositoryFile("src", "OpenBlade.Tray", "WinUiDispatcher.cs");
        Assert.Contains("dispatcherQueue.HasThreadAccess", dispatcher, StringComparison.Ordinal);
        Assert.Contains("token.ThrowIfCancellationRequested()", dispatcher, StringComparison.Ordinal);
        Assert.Contains("dispatcherQueue.TryEnqueue", dispatcher, StringComparison.Ordinal);
        Assert.Contains("completion.TrySetCanceled", dispatcher, StringComparison.Ordinal);
        Assert.Contains("completion.TrySetException(exception)", dispatcher, StringComparison.Ordinal);

        string trayHost = ReadRepositoryFile("src", "OpenBlade.Tray", "WinFormsTrayIconHost.cs");
        Assert.Contains("menu.Opening +=", trayHost, StringComparison.Ordinal);
        Assert.Contains("view.Enabled = model.IsEnabled", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.DoubleClick +=", trayHost, StringComparison.Ordinal);
        Assert.Contains("open.Invoke()", trayHost, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref disposed, 1)", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Visible = false", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Dispose()", trayHost, StringComparison.Ordinal);
        Assert.Contains("menu.Dispose()", trayHost, StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiAppRoutesEveryExitSourceThroughLifecycleAndGuardsActivation()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains("exitLifecycle.ExitAsync()", app, StringComparison.Ordinal);
        Assert.Contains("exitLifecycle.TryRunWhileActive", app, StringComparison.Ordinal);
        Assert.Contains("Trace.TraceError", app, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(app, "ExitTrayAsync()") >= 3,
            "The setup event, tray command, and shared method must all reference ExitTrayAsync().");
    }

    private static string ProjectPath => GetProjectPath();

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ProjectPath)!,
            "..",
            "..",
            Path.Combine(segments))));

    private static string GetProjectPath([CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            "..",
            "src",
            "OpenBlade.Tray",
            "OpenBlade.Tray.csproj"));

    private static int CountOccurrences(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
