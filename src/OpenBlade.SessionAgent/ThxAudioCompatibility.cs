using System.Security.Cryptography;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioPinnedFile(string Name, string Sha256);
internal sealed record ThxAudioDriverPackage(string InfName, IReadOnlyList<ThxAudioPinnedFile> Files);
internal sealed record ThxAudioDriverStack(
    string ServiceName,
    string ServiceExecutable,
    IReadOnlyList<ThxAudioDriverPackage> Packages);
internal sealed record ThxAudioCompatibilityProfile(
    string ModelNumber,
    string ProductName,
    string UsbProductId,
    string ProductId,
    IReadOnlyList<string> CodecHardwareIds,
    ThxAudioDriverStack Stack,
    bool ReadAllowed,
    bool MutationAllowed)
{
    public string ActivePropertiesSha256 { get; init; } = string.Empty;
    public string SurroundConfigSha256 { get; init; } = string.Empty;
}

internal static class ThxAudioCompatibility
{
    private const int MaximumPackageCandidates = 64;
    private const string CommonSurroundConfigSha256 = "1AC5DC1DD9B6E60843E6E3076DC94F4C2492D9DB02600442AF276FD6A9CCFD6C";

    private static readonly ThxAudioDriverStack Rznb12Stack = new(
        "thx_rznb12_service", "thx_rznb12_svc.exe",
        [
            new("thx_rznb12_apo4.inf",
            [
                new("thx_rznb12_apo4_data.bin", "A3E707295B5D83274F6CC2957B6620280D581B89D6EA91F1244898D90AC2B71A"),
                new("thx_rznb12_apo4.inf", "475EAA14B62F4C17DB5DCC89CB1F4A5381F8A3D6DE75E14DD92A4EC09002A914"),
                new("thx_rznb12_apo4.dll", "C8C1FCD4869DC7F384E69ACE5A320D9816FFC2D81C03D0667BC19644D9EF4122"),
                new("thx_rznb12_apo4_grpc.dll", "7A220E038B76F799ED079BBFBF864C4C30D16EE2777881A46FA0E7861EDC328B"),
                new("thx_rznb12_apo4.cat", "F7CA851E858BDA079B0B481D5BEDCD83EA8E5B18976D2A404B299AFDF95237D2"),
            ]),
            new("thx_rznb12_ext.inf",
            [
                new("thx_rznb12_ext.inf", "4559784C6F2C0951CCE99DFAE8E26321B2AF4E0C01BF717F3455354C455CAD5D"),
                new("thx_rznb12_ext.cat", "9754CAA20A26963F2512A215DDB1E09877265E25A011C5D283B6E8E9B2E9F242"),
            ]),
            new("thx_rznb12_svc.inf",
            [
                new("thx_rznb12_props.dat", "60177A678E167710A782024D1630275D9D55AAE637DACD07A35171AF03F0B748"),
                new("thx_rznb12_surround_714_patch.exe", "B5F57C12BE56298378FB6B89422D6988AE8CF1EBDB4A6B2DC85F3661FC14FCCF"),
                new("thx_rznb12_svc.inf", "0B312BA852C34727A0CD51BABCFE6B8E56E7A40D83BFB5BE8F9F8218B60AB610"),
                new("thx_rznb12_svc.exe", "BD1DC54D4ABECC67DD3A8FA25A39223577E035A623FEF38C915F70A659E8DD42"),
                new("thx_rznb12_svc.cat", "C8F088106319AF05D5D57D7A04BF918E19BF795AF62F0B6FA8B93C31D5CBF9C7"),
            ]),
        ]);

    // Official 4.2.29.1159 DriverStore package, paired below with the
    // reviewed 4.2.37.1845 active ProgramData configuration.
    private static readonly ThxAudioDriverStack Rznb11Stack = new(
        "thx_rznb11_service", "thx_rznb11_svc.exe",
        [
            new("thx_rznb11_apo4.inf",
            [
                new("thx_rznb11_apo4_data.bin", "A3E707295B5D83274F6CC2957B6620280D581B89D6EA91F1244898D90AC2B71A"),
                new("thx_rznb11_apo4_grpc.dll", "D2C727B6E9C0F47C81B445F3D51D1557EA88E36C04372F99169433B1B544BAF2"),
                new("thx_rznb11_apo4.cat", "9D56B7F7182E95841F321CF6E18C6AE0BE5BA71954988D260D3777660BF70994"),
                new("thx_rznb11_apo4.dll", "CBB41BF95AB27549B1053AE0366C2F65ADB683CDF63B0BBA5C3F80F071772C55"),
                new("thx_rznb11_apo4.inf", "F9171EDBE76F5C28E5B0CD75E5132EE390319D838957821BB4AE6A5B74CECD3C"),
            ]),
            new("thx_rznb11_ext.inf",
            [
                new("thx_rznb11_ext.cat", "FFF1BDAD9D9F685E1555A1765DA0C70E60EE6FEF9B02F1C55EF55F3C42DC19AD"),
                new("thx_rznb11_ext.inf", "C73EECF6526112174D1260E966F3463BC5BA69E3011AE254A5B7607601C56684"),
            ]),
            new("thx_rznb11_svc.inf",
            [
                new("thx_rznb11_props.dat", "2C5AB38CEB9A5FDD3962FCA12FC7E256DC2BA1A6C138D8DA2933CB74109E0D98"),
                new("thx_rznb11_surround_714_patch.exe", "968BFE48850238CB20C15DAE4689439343FE3C3A7A4E66D76A625E61A87248E1"),
                new("thx_rznb11_svc.cat", "CAC9F092D7F2D3BEF2032E937EEFD7A0EC6B23BBF73412C87D31A2B1E1D5ADF4"),
                new("thx_rznb11_svc.exe", "0037EC884CAB1EFFB17F6155B7394AFC581B4C084F27F062D914803BB895978B"),
                new("thx_rznb11_svc.inf", "0CA031CDB2CE9CE3124EA192F2216429580112061B8449E5E0DE7358E4F4750A"),
            ]),
        ]);

    // Sonya12's signed extension INF targets speaker child HardwareID values,
    // including DynamicEnumSpeaker0/AggregatedSpeaker. Parent SDCA IDs and the
    // Kira12 INTELAUDIO branch are deliberately excluded.
    private static readonly string[] Sonya12CodecIds =
    [
        @"SOUNDWIRE\SDCA_FUNCTION_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&SUBSYS_30101A58&AGGREGATEDSPEAKER",
        @"SOUNDWIRE\SDCA_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&SUBSYS_30101A58&AGGREGATEDSPEAKER",
        @"SOUNDWIRE\SDCA_FUNCTION_10&MAN_025D&FUNC_0721&TYPE_06&VER_01&ADR_01&LID_03&UID_00&SUBSYS_30101A58&DynamicEnumSpeaker0",
        @"SOUNDWIRE\SDCA_FUNCTION_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58&DYNAMICENUMSPEAKER0",
        @"SOUNDWIRE\SDCA_FUNCTION_10&MAN_025D&FUNC_1320&TYPE_01&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58&DYNAMICENUMSPEAKER0",
        @"SOUNDWIRE\SDCA_10&MAN_025D&FUNC_0721&TYPE_06&VER_01&ADR_01&LID_03&UID_00&SUBSYS_30101A58&DynamicEnumSpeaker0",
        @"SOUNDWIRE\SDCA_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&LID_03&UID_00&SUBSYS_30101A58&DynamicEnumSpeaker0",
    ];

    internal static IReadOnlyList<ThxAudioCompatibilityProfile> Profiles { get; } =
    [
        new("RZ09-0581", "Blade 16 - RZ09-0581", "02E0", "rznb12", Sonya12CodecIds, Rznb12Stack,
            ReadAllowed: true, MutationAllowed: true)
        {
            ActivePropertiesSha256 = "E2E69163AFFEAE1F4FD19E347EB92E63CA7CAA32C13E8E1500AA459D76823031",
            SurroundConfigSha256 = CommonSurroundConfigSha256,
        },
        // Manual controls use the reviewed shared vendor source contract.
        // Physical validation for these exact models remains pending.
        new("RZ09-0528", "Blade 16 - RZ09-0528", "02C6", "rznb11",
            [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A58300E"], Rznb11Stack,
            ReadAllowed: true, MutationAllowed: true)
        {
            ActivePropertiesSha256 = "4FDDEEF868014DB94B902475CA2DB1919E8A902D027CFCE8A7ED691E403319EF",
            SurroundConfigSha256 = CommonSurroundConfigSha256,
        },
        new("RZ09-0530", "Blade 14 - RZ09-0530", "02C5", "rznb11",
            [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A582023"], Rznb11Stack,
            ReadAllowed: true, MutationAllowed: true)
        {
            ActivePropertiesSha256 = "4FDDEEF868014DB94B902475CA2DB1919E8A902D027CFCE8A7ED691E403319EF",
            SurroundConfigSha256 = CommonSurroundConfigSha256,
        },
    ];

    internal static bool MatchesHost(ThxAudioCompatibilityProfile profile,
        string? biosManufacturer, string? biosProduct, string? systemManufacturer, string? systemProduct) =>
        string.Equals(biosManufacturer, "Razer", StringComparison.Ordinal)
        && string.Equals(systemManufacturer, "Razer", StringComparison.Ordinal)
        && string.Equals(biosProduct, profile.ProductName, StringComparison.Ordinal)
        && string.Equals(systemProduct, profile.ProductName, StringComparison.Ordinal);

    internal static bool MatchesUsb(ThxAudioCompatibilityProfile profile, string value)
    {
        string prefix = $"USB\\VID_1532&PID_{profile.UsbProductId}";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (value.Length == prefix.Length || value[prefix.Length] is '\\' or '&');
    }

    internal static bool MatchesCodec(ThxAudioCompatibilityProfile profile, IEnumerable<string> hardwareIds) =>
        hardwareIds.Any(actual => profile.CodecHardwareIds.Any(expected =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)));

    internal static bool HasPinnedActiveConfiguration(string commonApplicationData,
        ThxAudioCompatibilityProfile profile)
    {
        if (profile.ProductId is not ("rznb11" or "rznb12")
            || profile.ActivePropertiesSha256.Length != 64 || profile.SurroundConfigSha256.Length != 64
            || !profile.ActivePropertiesSha256.All(char.IsAsciiHexDigit)
            || !profile.SurroundConfigSha256.All(char.IsAsciiHexDigit))
        {
            return false;
        }
        string productDirectory = Path.Combine(commonApplicationData, "thx", profile.ProductId);
        // These two immutable definitions are deployed by the reviewed vendor
        // updaters. Mutable properties.json and preset_selection are excluded.
        return HasHash(Path.Combine(productDirectory, $"thx_{profile.ProductId}_props.dat"), profile.ActivePropertiesSha256)
            && HasHash(Path.Combine(productDirectory, "surround_714_config.json"), profile.SurroundConfigSha256);
    }

    internal static bool TryResolveDriverStack(string driverStore, ThxAudioDriverStack stack,
        out string? serviceBinary)
    {
        serviceBinary = null;
        try
        {
            string root = Path.GetFullPath(driverStore);
            foreach (ThxAudioDriverPackage package in stack.Packages)
            {
                string? matchedDirectory = null;
                int candidates = 0;
                foreach (string directory in Directory.EnumerateDirectories(root,
                    package.InfName + "_amd64_*", SearchOption.TopDirectoryOnly))
                {
                    if (++candidates > MaximumPackageCandidates)
                    {
                        return false;
                    }
                    // DriverStore suffixes are installation details. Every file
                    // must still match the reviewed package in this one folder.
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                        || !package.Files.All(file => HasHash(Path.Combine(directory, file.Name), file.Sha256)))
                    {
                        continue;
                    }
                    if (matchedDirectory is not null)
                    {
                        return false;
                    }
                    matchedDirectory = directory;
                }
                if (matchedDirectory is null)
                {
                    return false;
                }
                if (package.Files.Any(file => string.Equals(file.Name, stack.ServiceExecutable, StringComparison.Ordinal)))
                {
                    if (serviceBinary is not null)
                    {
                        return false;
                    }
                    serviceBinary = Path.Combine(matchedDirectory, stack.ServiceExecutable);
                }
            }
            return serviceBinary is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            serviceBinary = null;
            return false;
        }
    }

    internal static bool HasHash(string path, string expected)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }
}
