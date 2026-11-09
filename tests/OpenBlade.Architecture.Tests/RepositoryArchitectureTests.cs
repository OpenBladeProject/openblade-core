using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace OpenBlade.Architecture.Tests;

public sealed class RepositoryArchitectureTests
{
    [Fact]
    public void SourcesWireRealFeatureInteropOnlyThroughApprovedCompositionPoints()
    {
        string sourceDirectory = Path.Combine(GetRepositoryPath(), "src");
        string separator = Path.DirectorySeparatorChar.ToString();
        string[] sourceFiles = Directory
            .GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{separator}bin{separator}",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sources = sourceFiles.ToDictionary(path => path, File.ReadAllText);

        Assert.DoesNotContain(sources.Values, source => source.Contains("HidD_SetFeature"));
        Assert.DoesNotContain(sources.Values, source => source.Contains("HidD_GetFeature"));
        Assert.DoesNotContain(
            sources.Where(pair =>
                !pair.Key.EndsWith("WindowsHidEnumerator.cs")
                && !pair.Key.EndsWith("BatteryValidationFactory.cs")
                && !pair.Key.EndsWith("ProductionBatteryFactory.cs")
                && !pair.Key.EndsWith("ProductionLightingFactory.cs")
                // The delegated guardian reader admits only the exact-model
                // adapter getter; its factory has no setter fixtures.
                && pair.Key != Path.Combine(sourceDirectory, "OpenBlade.Protocol", "FanStopAdapterPowerReader.cs")
                && pair.Key != Path.Combine(sourceDirectory, "OpenBlade.Protocol", "ExperimentalBladeFactory.cs")
                && !pair.Key.EndsWith("GetterCandidateProbeRunner.cs")
                && pair.Key != Path.Combine(sourceDirectory, "OpenBlade.Protocol", "Rz090528HyperBoostValidation.cs")
                && pair.Key != Path.Combine(sourceDirectory, "OpenBlade.Protocol", "Rz090528PadlessHyperBoostValidation.cs")
                // The attended Capture runner uses only candidate identity metadata;
                // this does not admit another feature-report composition point.
                && pair.Key != Path.Combine(sourceDirectory, "OpenBlade.FanStop.Core", "So6120WindowsSupervisedMetadata.cs")
                && !pair.Key.EndsWith("HidInputAccessProbe.cs")
                && pair.Key != Path.Combine(sourceDirectory, "OpenBlade.Protocol", "WindowsCoolingPadDeviceProvider.cs")),
            pair => pair.Value.Contains("new WindowsHidNative("));
        string[] composition = sources
            .Where(pair => pair.Value.Contains("OverlappedFeatureOperation")
                && pair.Value.Contains("WindowsHidNative")
                && !pair.Key.EndsWith("WindowsHidNative.cs"))
            .Select(pair => Path.GetFileName(pair.Key))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "BatteryValidationFactory.cs",
                "ExperimentalBladeFactory.cs",
                "FanStopAdapterPowerReader.cs",
                "GetterCandidateProbeRunner.cs",
                "ProductionBatteryFactory.cs",
                "ProductionLightingFactory.cs",
                "Rz090528HyperBoostValidation.cs",
                "Rz090528PadlessHyperBoostValidation.cs",
                "WindowsCoolingPadDeviceProvider.cs",
            ],
            composition);
        string candidateProbePath = Assert.Single(
            sources.Keys,
            path => path.EndsWith("GetterCandidateProbeRunner.cs"));
        Assert.Contains(
            $"{separator}OpenBlade.Capture{separator}",
            candidateProbePath,
            StringComparison.OrdinalIgnoreCase);
        string admission = sources[Path.Combine(sourceDirectory, "OpenBlade.FanStop.Core", "So6120WindowsSupervisedMetadata.cs")];
        Assert.Contains("ReadHidMetadata(IReadOnlyList<string> paths, IHidCandidateNative native,", admission, StringComparison.Ordinal);
        Assert.Contains("FanStopModelContract? serviceContract = null)", admission, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlappedFeatureOperation", admission, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceAndTrayUseTheSameExactControllerAllowlist()
    {
        string repository = GetRepositoryPath();
        string[] service = ReadAllowlist(
            Path.Combine(repository, "src", "OpenBlade.Service", "ServiceControlRuntime.cs"),
            "CompetingControllerProcesses");
        string[] tray = ReadAllowlist(
            Path.Combine(repository, "src", "OpenBlade.Tray.Core", "MainViewModel.cs"),
            "CompetingLightingControllers");
        var expected = new HashSet<string>(
            ["RazerAppEngine", "Razer Synapse Service"],
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Count, service.Length);
        Assert.Equal(expected.Count, tray.Length);
        Assert.True(expected.SetEquals(service));
        Assert.True(expected.SetEquals(tray));
    }

    [Fact]
    public void EfficientProcessorPolicyIsLimitedToTheTray()
    {
        string repository = GetRepositoryPath();
        string policy = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "OpenBlade.Platform",
            "WindowsProcessorEfficiencyPolicy.cs"));

        foreach (string project in new[] { "OpenBlade.Service", "OpenBlade.SessionAgent" })
        {
            string projectDirectory = Path.Combine(repository, "src", project);
            foreach (string sourcePath in Directory.EnumerateFiles(
                projectDirectory,
                "*.cs",
                SearchOption.AllDirectories).Where(path =>
                    !path.Contains(
                        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase)
                    && !path.Contains(
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase)))
            {
                Assert.DoesNotContain(
                    "WindowsProcessorEfficiencyPolicy",
                    File.ReadAllText(sourcePath),
                    StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("SetProcessDefaultCpuSets", policy, StringComparison.Ordinal);
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
