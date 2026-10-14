namespace OpenBlade.Contracts;

public sealed record RazerBladeModelInfo(
    string ModelNumber,
    string ProductName,
    int ModelYear)
{
    public string UsbVendorId { get; init; } = string.Empty;

    public string UsbProductId { get; init; } = string.Empty;

    public string DisplayName => $"{ProductName} ({ModelYear})";
}

public static class RazerBladeModelCatalog
{
    public const string OpenBladeTargetModelNumber = "RZ09-0581";
    public const string Rz090528ModelNumber = "RZ09-0528";

    // Razer support Answer ID 5481: How to identify a Razer Blade model.
    private static readonly RazerBladeModelInfo[] Models =
    [
        new(OpenBladeTargetModelNumber, "Razer Blade 16", 2026)
        {
            UsbVendorId = "1532",
            UsbProductId = "02E0",
        },
        new(Rz090528ModelNumber, "Razer Blade 16", 2025)
        {
            UsbVendorId = "1532",
            UsbProductId = "02C6",
        },
        new("RZ09-0483", "Razer Blade 16", 2023) { UsbVendorId = "1532", UsbProductId = "029F" },
        new("RZ09-0482", "Razer Blade 14", 2023) { UsbVendorId = "1532", UsbProductId = "029D" },
        new("RZ09-0530", "Razer Blade 14", 2025) { UsbVendorId = "1532", UsbProductId = "02C5" },
        new("RZ09-0328", "Razer Blade 15 Base", 2020) { UsbVendorId = "1532", UsbProductId = "0255" },
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
