namespace OpenBlade.Tray;

/// <summary>Immutable installation inputs, independent of compatible installed-driver admission.</summary>
internal sealed class RazerKeyboardDriverRecipe
{
    internal RazerKeyboardDriverRecipe(string id, string productName, string productId, string deviceService,
        string deviceHash, string archiveName, long archiveLength, string archiveSha256,
        string deviceCatalogSignerThumbprint, IEnumerable<RazerKeyboardDriverPackage.PackageFile> files,
        IEnumerable<string> stageOrder, IEnumerable<string> installOrder,
        Uri? downloadUri = null, IEnumerable<RazerDriverArchiveBlock>? vendorBlocks = null,
        string? carrierSignerSubject = null, string? carrierSignerThumbprint = null, int vendorHeaderOffset = 0)
    {
        Id = id;
        ProductName = productName;
        ProductId = productId;
        DeviceService = deviceService;
        DeviceHash = deviceHash;
        ArchiveName = archiveName;
        ArchiveLength = archiveLength;
        ArchiveSha256 = archiveSha256;
        PackageDirectory = Path.GetFileNameWithoutExtension(archiveName);
        DownloadUri = downloadUri ?? new Uri($"https://github.com/OpenBladeProject/blade-driver-catalog/releases/download/{id}/{archiveName}");
        CarrierSignerSubject = carrierSignerSubject;
        CarrierSignerThumbprint = carrierSignerThumbprint;
        VendorHeaderOffset = vendorHeaderOffset;
        VendorBlocks = vendorBlocks is null ? null : Array.AsReadOnly(vendorBlocks.ToArray());
        DeviceCatalogSignerThumbprint = deviceCatalogSignerThumbprint;
        Files = Array.AsReadOnly(files.ToArray());
        StageOrder = Array.AsReadOnly(stageOrder.ToArray());
        InstallOrder = Array.AsReadOnly(installOrder.ToArray());
    }

    public string? CarrierSignerSubject { get; }
    public string? CarrierSignerThumbprint { get; }
    public int VendorHeaderOffset { get; }
    public IReadOnlyList<RazerDriverArchiveBlock>? VendorBlocks { get; }
    public string InfFor(string part) => StageOrder.Single(path => part == "common"
        ? Path.GetFileName(path).Equals("rzcommonu.inf", StringComparison.OrdinalIgnoreCase)
        : path.EndsWith($"_{part}.inf", StringComparison.OrdinalIgnoreCase));

    public string Id { get; }
    public string ProductName { get; }
    public string ProductId { get; }
    public string DeviceService { get; }
    public string CommonHash => "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59";
    public string DeviceHash { get; }
    public string ArchiveName { get; }
    public long ArchiveLength { get; }
    public string ArchiveSha256 { get; }
    public string PackageDirectory { get; }
    public Uri DownloadUri { get; }
    public IReadOnlyList<RazerKeyboardDriverPackage.PackageFile> Files { get; }
    public IReadOnlyList<string> StageOrder { get; }
    public IReadOnlyList<string> InstallOrder { get; }
    public string CommonCatalogSignerThumbprint => "FAC666005546D6BE881A31C1267717879401A950";
    public string DeviceCatalogSignerThumbprint { get; }
}

internal sealed record RazerDriverArchiveBlock(string RelativePath, int Offset, int CompressedLength, bool Compressed);

internal static partial class RazerKeyboardDriverRecipes
{
    public static RazerKeyboardDriverRecipe Pid02E0 { get; } = new(
        RazerKeyboardDriverPackage.ReleaseTag, "Blade 16 - RZ09-0581", "02E0", "RzDev_02e0",
        "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787",
        RazerKeyboardDriverPackage.ArchiveName, RazerKeyboardDriverPackage.ArchiveLength,
        RazerKeyboardDriverPackage.ArchiveSha256, "32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21",
        RazerKeyboardDriverPackage.Files, RazerKeyboardDriverPackage.StageOrder, RazerKeyboardDriverPackage.InstallOrder);

    // Exact catalog revision 643d6985cd0a4357c5f6a99dea76087fb95850e6 and its unchanged
    // published archive. All 19 members (including notices) are independently pinned.
    public static RazerKeyboardDriverRecipe Pid02C6 { get; } = new(
        "rz09-0528-1532-02c6-driver-stack-1.0.0.78-1.0.0.76", "Blade 16 - RZ09-0528", "02C6", "RzDev_02c6",
        "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82",
        "Razer-02C6-Driver-Stack-1.0.0.78-1.0.0.76.zip", 227351,
        "533F8464A6A3ED328ABDADAC2DF2619859A6151FC17E775F46FCE310A682F8C5",
        "1F4DB7FDCCB0E6292AA2E70436EF68466E8674EA",
        [
            new("manifest.json", 8747, "35235DF0C7FEC30AAC1544C78931232FB3627C3A45447CE75F34D44654E593F1"),
            new("README.txt", 369, "6F51C8D5808E633459D2E0018CC78B56DA8E1718C4643080CF2BFB5481D33DAE"),
            new("SHA256SUMS.txt", 1719, "C6A35BB053F2BAEEB1DF7E0D940CC45A5912C8320A59DA5DCB74CF917AFB22D5"),
            new("THIRD-PARTY-NOTICE.txt", 576, "7D9628D1073F6733461EC60C45F2312ED9CDDF14AF3A1D9B3BC44E2F041DA564"),
            new("rzcommonu/rzcommonu.inf", 1646, "DD3C8D65C7C3CCF2093FE70FB21FB8DC724E837880566BAE629B3104A5B1B37B"),
            new("rzcommonu/rzcommon.cat", 13844, "1DFBA6428F727EA414F0FB5E938EE76063B1193A6CC12895396C9B89486C6F6E"),
            new("rzcommonu/RzCommon.sys", 64880, "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59"),
            new("rzdevu_02c6_dkm/rzdevu_02c6_dkm.inf", 1835, "0304D1C4E9D946A975B53630711C5CD41CFD559A5F00CBE8A382C715D78653FA"),
            new("rzdevu_02c6_dkm/rzdev_02c6_dkm.cat", 11771, "AF736ED596A12FCACA774F2C6660D6ACFA630930125C4295F19A7A923D4850E2"),
            new("rzdevu_02c6_dkm/RzDev_02c6.sys", 65296, "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82"),
            new("rzdevu_02c6_vcon/rzdevu_02c6_vcon.inf", 1893, "3C36041A6BADB66A02BCA23643046766F49E2D93D88EEFAD9D9C561659F36902"),
            new("rzdevu_02c6_vcon/rzdev_02c6_vcon.cat", 11775, "F91B9B794DAB2379922F5B6EE30C1E7B92D7FB5BBD68EFCF26D7E53EF580C290"),
            new("rzdevu_02c6_vcon/RzDev_02c6.sys", 65296, "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82"),
            new("rzdevu_02c6_kbd/rzdevu_02c6_kbd.inf", 1980, "4202175393D583B1DE5367F73996F5EC6F635F041E7F9A4AB8ECAFC0AC03E031"),
            new("rzdevu_02c6_kbd/rzdev_02c6_kbd.cat", 11771, "067A5B6506DE180E6676DDBD9A37C3DB6B736AA4699EC0E896B387432EFA89CD"),
            new("rzdevu_02c6_kbd/RzDev_02c6.sys", 65296, "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82"),
            new("rzdevu_02c6_mou/rzdevu_02c6_mou.inf", 2032, "A724F43D4903A32C5BB682FED80048F41B808963B2EC3A10E8736A248F27FFAC"),
            new("rzdevu_02c6_mou/rzdev_02c6_mou.cat", 11771, "0F6362123404D848C6AF04B94F6C01B69E66616D91CC362B4204007DA9A24159"),
            new("rzdevu_02c6_mou/RzDev_02c6.sys", 65296, "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82"),
        ],
        // DKM creates the virtual consumer child; the mouse filter's ControlDevice=1
        // creates the RzCommon child. Stage both child packages before binding parents.
        ["rzdevu_02c6_vcon/rzdevu_02c6_vcon.inf", "rzcommonu/rzcommonu.inf",
         "rzdevu_02c6_dkm/rzdevu_02c6_dkm.inf", "rzdevu_02c6_kbd/rzdevu_02c6_kbd.inf",
         "rzdevu_02c6_mou/rzdevu_02c6_mou.inf"],
        ["rzdevu_02c6_dkm/rzdevu_02c6_dkm.inf", "rzdevu_02c6_vcon/rzdevu_02c6_vcon.inf",
         "rzdevu_02c6_kbd/rzdevu_02c6_kbd.inf", "rzdevu_02c6_mou/rzdevu_02c6_mou.inf",
         "rzcommonu/rzcommonu.inf"]);

    // The five vendor recipes live in another partial file. Build the registry only
    // after every field initializer has run; source-file ordering must not admit nulls.
    static RazerKeyboardDriverRecipes()
    {
        All = Array.AsReadOnly(new[] { Pid02E0, Pid02C6, Pid029F, Pid029D, Pid02C5, Pid0255, Pid02A0 });
    }

    public static IReadOnlyList<RazerKeyboardDriverRecipe> All { get; }

    public static RazerKeyboardDriverRecipe? FindById(string? id) =>
        All.SingleOrDefault(recipe => string.Equals(recipe.Id, id, StringComparison.Ordinal));

    public static RazerKeyboardDriverRecipe? FindHost(string? biosManufacturer, string? biosProduct,
        string? systemManufacturer, string? systemProduct) =>
        biosManufacturer == "Razer" && systemManufacturer == "Razer" && biosProduct == systemProduct
            ? All.SingleOrDefault(recipe => recipe.ProductName == biosProduct)
            : null;
}
