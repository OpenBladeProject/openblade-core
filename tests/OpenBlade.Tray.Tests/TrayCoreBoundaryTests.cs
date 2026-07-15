using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class TrayCoreBoundaryTests
{
    [Fact]
    public void CoreProjectExistsAndContainsNoDesktopUiFrameworkReference()
    {
        string repository = GetRepositoryPath();
        string projectPath = Path.Combine(
            repository, "src", "OpenBlade.Tray.Core", "OpenBlade.Tray.Core.csproj");

        Assert.True(File.Exists(projectPath), $"Missing {projectPath}");
        string project = File.ReadAllText(projectPath);
        Assert.DoesNotContain("UseWPF", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWinUI", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWindowsForms", project, StringComparison.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(
                     Path.GetDirectoryName(projectPath)!, "*.cs"))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("using System.Windows;", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows.Threading", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows.Controls", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.UI", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows.Forms", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Wpf", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetRepositoryPath([CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", ".."));
}
