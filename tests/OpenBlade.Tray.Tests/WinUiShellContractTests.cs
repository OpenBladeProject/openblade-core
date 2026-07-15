using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class WinUiShellContractTests
{
    [Fact]
    public void TrayExecutableIsPinnedUnpackagedSelfContainedWinUiAndContainsNoWpf()
    {
        string project = ReadRepositoryFile("src", "OpenBlade.Tray", "OpenBlade.Tray.csproj");
        Assert.Contains("<UseWinUI>true</UseWinUI>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"Microsoft.WindowsAppSDK\" Version=\"2.2.0\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWPF", project, StringComparison.OrdinalIgnoreCase);

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
}
