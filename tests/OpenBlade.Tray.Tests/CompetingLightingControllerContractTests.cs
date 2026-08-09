using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace OpenBlade.Tray.Tests;

public sealed class CompetingLightingControllerContractTests
{
    [Fact]
    public void ServiceAndTrayUseTheSameExactControllerAllowlist()
    {
        string repository = GetRepositoryPath();
        string[] service = ReadAllowlist(
            Path.Combine(
                repository,
                "src",
                "OpenBlade.Service",
                "ServiceControlRuntime.cs"),
            "CompetingControllerProcesses");
        string[] tray = ReadAllowlist(
            Path.Combine(
                repository,
                "src",
                "OpenBlade.Tray.Core",
                "MainViewModel.cs"),
            "CompetingLightingControllers");
        var expected = new HashSet<string>(
            ["RazerAppEngine", "Razer Synapse Service"],
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Count, service.Length);
        Assert.Equal(expected.Count, tray.Length);
        Assert.True(expected.SetEquals(service));
        Assert.True(expected.SetEquals(tray));
    }

    private static string[] ReadAllowlist(string path, string fieldName)
    {
        string source = File.ReadAllText(path);
        Match initializer = Regex.Match(
            source,
            $@"\b{Regex.Escape(fieldName)}\b(?<body>[\s\S]*?);",
            RegexOptions.CultureInvariant);
        Assert.True(initializer.Success, $"Missing {fieldName} in {path}.");

        return Regex.Matches(
                initializer.Groups["body"].Value,
                "\"(?<value>[^\"]+)\"",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["value"].Value)
            .ToArray();
    }

    private static string GetRepositoryPath([CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", ".."));
}
