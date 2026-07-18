namespace OpenBlade.Contracts;

public sealed record RazerBladeModelInfo(
    string ModelNumber,
    string ProductName,
    int ModelYear)
{
    public string DisplayName => $"{ProductName} ({ModelYear})";
}

public static class RazerBladeModelCatalog
{
    public const string OpenBladeTargetModelNumber = "RZ09-0581";

    // Razer support Answer ID 5481: How to identify a Razer Blade model.
    private static readonly RazerBladeModelInfo[] Models =
    [
        new(OpenBladeTargetModelNumber, "Razer Blade 16", 2026),
    ];

    public static RazerBladeModelInfo? Find(string? modelNumber)
    {
        if (string.IsNullOrWhiteSpace(modelNumber))
        {
            return null;
        }

        string candidate = modelNumber.Trim();
        return Models.FirstOrDefault(model => candidate.StartsWith(
            model.ModelNumber,
            StringComparison.OrdinalIgnoreCase));
    }
}
