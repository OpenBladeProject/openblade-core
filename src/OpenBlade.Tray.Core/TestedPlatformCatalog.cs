using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record TestedPlatformVersionSet(
    string ModelNumber,
    IReadOnlyList<string> BiosVersions,
    IReadOnlyList<string> EcVersions,
    IReadOnlyList<string> McuVersions);

public sealed record TestedPlatformVersionMismatch(
    string Component,
    string CurrentValue,
    IReadOnlyList<string> TestedValues);

public sealed record TestedPlatformAssessment(
    bool IsEvaluated,
    IReadOnlyList<TestedPlatformVersionMismatch> Mismatches)
{
    public bool MatchesTestedConfiguration => IsEvaluated && Mismatches.Count == 0;
}

public static class TestedPlatformCatalog
{
    private static readonly TestedPlatformVersionSet[] VersionSets =
    [
        new(
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            Array.AsReadOnly(new[] { "3.01", "4.00", "4.01" }),
            Array.AsReadOnly(new[] { "1.04" }),
            Array.AsReadOnly(new[] { "1.5.0.0", "1.6.0.0", "1.7.0.0" })),
        new(
            RazerBladeModelCatalog.Rz090528ModelNumber,
            Array.AsReadOnly(new[] { "2.02" }),
            Array.AsReadOnly(new[] { "1.09" }),
            Array.AsReadOnly(new[] { "1.9.0.0" })),
    ];

    public static IReadOnlyList<TestedPlatformVersionSet> Entries { get; } =
        Array.AsReadOnly(VersionSets);

    public static TestedPlatformVersionSet OpenBladeTarget => VersionSets[0];

    public static TestedPlatformAssessment Assess(
        string? modelNumber,
        FirmwareVersions? firmware)
    {
        if (string.IsNullOrWhiteSpace(modelNumber))
        {
            return new(false, []);
        }

        string currentModel = modelNumber.Trim();
        TestedPlatformVersionSet? tested = VersionSets.FirstOrDefault(entry =>
            currentModel.StartsWith(
                entry.ModelNumber,
                StringComparison.OrdinalIgnoreCase));
        if (tested is null)
        {
            return new(
                true,
                [
                    new(
                        "Model",
                        currentModel,
                        VersionSets.Select(entry => entry.ModelNumber).ToArray()),
                ]);
        }

        var mismatches = new List<TestedPlatformVersionMismatch>();
        AddMismatch(
            mismatches,
            "BIOS",
            firmware?.BiosVersion,
            tested.BiosVersions);
        AddMismatch(
            mismatches,
            "EC",
            firmware?.EcVersion,
            tested.EcVersions);
        AddMismatch(
            mismatches,
            "MCU",
            firmware?.McuVersion,
            tested.McuVersions);
        return new(true, mismatches);
    }

    private static void AddMismatch(
        ICollection<TestedPlatformVersionMismatch> mismatches,
        string component,
        string? currentValue,
        IReadOnlyList<string> testedValues)
    {
        string current = string.IsNullOrWhiteSpace(currentValue)
            ? "Unavailable"
            : currentValue.Trim();
        if (testedValues.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        mismatches.Add(new(component, current, testedValues));
    }
}
