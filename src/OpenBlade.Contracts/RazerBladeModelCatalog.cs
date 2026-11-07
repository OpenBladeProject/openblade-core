namespace OpenBlade.Contracts;

public enum BladeSupportBasis
{
    NotEstablished,
    SourceReviewed,
    PhysicallyValidated,
}

public sealed record RazerBladeModelInfo(
    string ModelNumber,
    string ProductName,
    int ModelYear)
{
    public string UsbVendorId { get; init; } = string.Empty;

    public string UsbProductId { get; init; } = string.Empty;

    // Model support and physical evidence are separate from per-feature admission.
    // Neither property authorizes a hardware operation.
    public BladeSupportBasis SupportBasis { get; init; }

    public bool IsOfficiallySupported => SupportBasis is
        BladeSupportBasis.SourceReviewed or BladeSupportBasis.PhysicallyValidated;

    public bool HasPhysicalValidation => SupportBasis == BladeSupportBasis.PhysicallyValidated;

    public string DisplayName => $"{ProductName} ({ModelYear})";
}

public static class RazerBladeModelCatalog
{
    public const string OpenBladeTargetModelNumber = "RZ09-0581";
    public const string Rz090528ModelNumber = "RZ09-0528";
    public const string Rz090484ModelNumber = "RZ09-0484";

    // Razer support Answer ID 5481: How to identify a Razer Blade model.
    private static readonly RazerBladeModelInfo[] Models =
    [
        new(OpenBladeTargetModelNumber, "Razer Blade 16", 2026)
        {
            UsbVendorId = "1532",
            UsbProductId = "02E0",
            SupportBasis = BladeSupportBasis.PhysicallyValidated,
        },
        new(Rz090528ModelNumber, "Razer Blade 16", 2025)
        {
            UsbVendorId = "1532",
            UsbProductId = "02C6",
            SupportBasis = BladeSupportBasis.PhysicallyValidated,
        },
        new(Rz090484ModelNumber, "Razer Blade 18", 2023) { UsbVendorId = "1532", UsbProductId = "02A0", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0483", "Razer Blade 16", 2023) { UsbVendorId = "1532", UsbProductId = "029F", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0482", "Razer Blade 14", 2023) { UsbVendorId = "1532", UsbProductId = "029D", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0530", "Razer Blade 14", 2025) { UsbVendorId = "1532", UsbProductId = "02C5", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0328", "Razer Blade 15 Base", 2020) { UsbVendorId = "1532", UsbProductId = "0255", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0508", "Razer Blade 14", 2024) { UsbVendorId = "1532", UsbProductId = "02B6", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0510", "Razer Blade 16", 2024) { UsbVendorId = "1532", UsbProductId = "02B7", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0509", "Razer Blade 18", 2024) { UsbVendorId = "1532", UsbProductId = "02B8", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0529", "Razer Blade 18", 2025) { UsbVendorId = "1532", UsbProductId = "02C7", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0370", "Razer Blade 14", 2021) { UsbVendorId = "1532", UsbProductId = "0270", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0427", "Razer Blade 14", 2022) { UsbVendorId = "1532", UsbProductId = "028C", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0421", "Razer Blade 15", 2022) { UsbVendorId = "1532", UsbProductId = "028A", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0423", "Razer Blade 17", 2022) { UsbVendorId = "1532", UsbProductId = "028B", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0330", "Razer Blade 15 Advanced", 2020) { UsbVendorId = "1532", UsbProductId = "0253", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0351", "Razer Blade 15 Base", 2020) { UsbVendorId = "1532", UsbProductId = "0268", SupportBasis = BladeSupportBasis.SourceReviewed },
        new("RZ09-0300", "Razer Blade 15 Base", 2019) { UsbVendorId = "1532", UsbProductId = "0246", SupportBasis = BladeSupportBasis.SourceReviewed },
    ];

    public static IReadOnlyList<RazerBladeModelInfo> All { get; } = Array.AsReadOnly(Models);

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
