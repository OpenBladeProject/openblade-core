using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsHostIdentitySourceTests
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey = @"SYSTEM\CurrentControlSet\Control\SystemInformation";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private static readonly WindowsPlatformEvidence ExactPlatform = new(
        IsWindows: true,
        IsWorkstation: true,
        Version: new Version(10, 0, 26200),
        Architecture.X64);

    private static readonly RegistryValueRequest[] ExpectedRequests =
    [
        new(BiosKey, "SystemManufacturer", "Razer"),
        new(BiosKey, "SystemProductName", "Blade 16 - RZ09-0581"),
        new(BiosKey, "SystemSKU", "RZ09-05819EN4"),
        new(BiosKey, "BIOSVersion", "3.01"),
        new(SystemInformationKey, "SystemManufacturer", "Razer"),
        new(SystemInformationKey, "SystemProductName", "Blade 16 - RZ09-0581"),
        new(SystemInformationKey, "BIOSVersion", "3.01"),
    ];

    [Fact]
    public void WindowsRegistryValueSource_ReturnsNullForMissingKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new WindowsRegistryValueSource();

        object? value = source.Read(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\OpenBlade\A-Key-That-Does-Not-Exist",
            "MissingValue");

        Assert.Null(value);
    }

    public static TheoryData<string, string> RegistryValues => CreateRequestData(
        request => $"{request.ExpectedValue}-wrong");

    public static TheoryData<string, string> CaseDifferentRegistryValues
    {
        get
        {
            TheoryData<string, string> data = new();
            foreach (RegistryValueRequest request in RequiredIdentityRequests)
            {
                string caseDifferentValue = request.ExpectedValue.ToLowerInvariant();
                if (!string.Equals(caseDifferentValue, request.ExpectedValue, StringComparison.Ordinal))
                {
                    data.Add(request.Location, caseDifferentValue);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> RegistryValueLocations()
    {
        TheoryData<string> data = new();
        foreach (RegistryValueRequest request in RequiredIdentityRequests)
        {
            data.Add(request.Location);
        }

        return data;
    }

    public static TheoryData<string> OverlappingRegistryValues => new()
    {
        { $"{SystemInformationKey}|SystemManufacturer" },
        { $"{SystemInformationKey}|SystemProductName" },
        { $"{SystemInformationKey}|BIOSVersion" },
    };

    [Fact]
    public void ReadExactIdentity_AcceptsOnlyCompleteExactEvidence()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        WindowsHostIdentitySource source = CreateSource(registry);

        HostIdentityReadResult result = source.ReadExactIdentity();

        Assert.Equal(
            new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "3.01"),
            result.Evidence);
        Assert.Null(result.Failure);
        Assert.Null(result.HostIdentityFailure);
        Assert.Equal(HostFirmwareCompatibility.Tested, result.FirmwareCompatibility);
        Assert.Equal(
            ExpectedRequests.Select(request => request.Location).Order(),
            registry.Requests.Select(request => request.Location).Order());
        Assert.All(registry.Requests, request =>
        {
            Assert.Equal(RegistryHive.LocalMachine, request.Hive);
            Assert.Equal(RegistryView.Registry64, request.View);
        });
    }

    [Theory]
    [InlineData("3.01")]
    [InlineData("4.00")]
    [InlineData("4.01")]
    public void ReadExactIdentity_AcceptsEachSupportedProductionBios(string biosVersion)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|BIOSVersion"] = biosVersion;
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = biosVersion;

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        Assert.Equal(biosVersion, result.Evidence?.BiosVersion);
        Assert.Null(result.Failure);
        Assert.Equal(HostFirmwareCompatibility.Tested, result.FirmwareCompatibility);
    }

    [Fact]
    public void ReadExactIdentity_Rz090528UsesIndependentFirmwareSet()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemProductName"] = "Blade 16 - RZ09-0528";
        registry.Values[$"{BiosKey}|SystemSKU"] = "RZ09-05289EN9";
        registry.Values[$"{BiosKey}|BIOSVersion"] = "2.02";
        registry.Values[$"{SystemInformationKey}|SystemProductName"] =
            "Blade 16 - RZ09-0528";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "2.02";
        var source = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            WindowsHostIdentitySource.Rz090528Identity);

        HostIdentityReadResult result = source.ReadExactIdentity();

        Assert.Equal(
            new HostIdentityEvidence(
                "Razer",
                "Blade 16 - RZ09-0528",
                "RZ09-05289EN9",
                "2.02"),
            result.Evidence);
        Assert.Null(result.Failure);
        Assert.Equal(
            HostFirmwareCompatibility.Tested,
            result.FirmwareCompatibility);

        registry.Values[$"{BiosKey}|BIOSVersion"] = "4.00";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "4.00";

        HostIdentityReadResult modelScoped = source.ReadExactIdentity();

        Assert.Equal("4.00", modelScoped.Evidence?.BiosVersion);
        Assert.Null(modelScoped.Failure);
        Assert.Equal(
            HostFirmwareCompatibility.ModelScoped,
            modelScoped.FirmwareCompatibility);
    }

    [Fact]
    public void ReadExactIdentity_UntestedFirmwareRequiresExplicitPolicy()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|BIOSVersion"] = "3.02";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "3.02";
        var policy = new FirmwareCompatibilityPolicy();
        var expected = new ExpectedHostIdentity(
            "Razer",
            "Blade 16 - RZ09-0581",
            "RZ09-05819EN4",
            ["3.01", "4.00", "4.01"]);
        var source = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            expected,
            policy);

        HostIdentityReadResult blocked = source.ReadExactIdentity();
        AssertIdentityBlocked(blocked);
        Assert.Equal(
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.FirmwareNotAdmitted,
                "BIOS",
                "BIOSVersion",
                "3.01, 4.00, 4.01",
                "3.02"),
            blocked.HostIdentityFailure);
        Assert.Equal(
            HostFirmwareCompatibility.UntestedBlocked,
            blocked.FirmwareCompatibility);

        policy.AllowUntestedFirmware = true;
        HostIdentityReadResult result = source.ReadExactIdentity();

        Assert.Equal("3.02", result.Evidence?.BiosVersion);
        Assert.Null(result.Failure);
        Assert.Equal(
            HostFirmwareCompatibility.UntestedAllowed,
            result.FirmwareCompatibility);
    }

    [Fact]
    public void ReadExactIdentity_UnknownProductionBiosIsModelScopedWithoutOverride()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|BIOSVersion"] = "99.99";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "99.99";

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        Assert.Equal("99.99", result.Evidence?.BiosVersion);
        Assert.Null(result.Failure);
        Assert.Null(result.HostIdentityFailure);
        Assert.Equal(HostFirmwareCompatibility.ModelScoped, result.FirmwareCompatibility);
    }

    [Fact]
    public void ReadExactIdentity_UntestedFirmwarePolicyDoesNotRelaxHardwareIdentity()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemProductName"] = "Blade 16 - RZ09-9999";
        registry.Values[$"{SystemInformationKey}|SystemProductName"] = "Blade 16 - RZ09-9999";
        var policy = new FirmwareCompatibilityPolicy { AllowUntestedFirmware = true };

        HostIdentityReadResult result = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            policy).ReadExactIdentity();

        AssertIdentityBlocked(result);
        Assert.Equal(HostFirmwareCompatibility.Unknown, result.FirmwareCompatibility);
    }

    [Fact]
    public void ReadExactIdentity_UntestedFirmwarePolicyStillRequiresCrossKeyAgreement()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|BIOSVersion"] = "3.02";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "3.03";
        var policy = new FirmwareCompatibilityPolicy { AllowUntestedFirmware = true };

        HostIdentityReadResult result = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            policy).ReadExactIdentity();

        AssertIdentityBlocked(result);
    }

    [Theory]
    [MemberData(nameof(RegistryValues))]
    public void ReadExactIdentity_BlocksEveryMismatchedValue(string location, string wrongValue)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[location] = wrongValue;

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
    }

    [Fact]
    public void ReadExactIdentity_AcceptsProductionEn9SkuWithMatchingModelAndBios()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemSKU"] = "RZ09-05819EN9";
        registry.Values[$"{BiosKey}|BIOSVersion"] = "4.01";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "4.01";

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        Assert.Equal("RZ09-05819EN9", result.Evidence?.Sku);
        Assert.Null(result.Failure);
        Assert.Null(result.HostIdentityFailure);
        Assert.Equal(HostFirmwareCompatibility.Tested, result.FirmwareCompatibility);
        Assert.Equal(
            ExpectedRequests.Select(request => request.Location),
            registry.Requests.Select(request => request.Location));
    }

    [Fact]
    public void ReadExactIdentity_ExplicitExactVersionIdentityStillRequiresSku()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemSKU"] = "RZ09-05819EN9";
        var expected = new ExpectedHostIdentity(
            "Razer",
            "Blade 16 - RZ09-0581",
            "RZ09-05819EN4",
            ["3.01", "4.00", "4.01"]);
        var source = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            expected);

        HostIdentityReadResult result = source.ReadExactIdentity();

        AssertIdentityBlocked(result);
        Assert.Equal(
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistryValueMismatch,
                "BIOS",
                "SystemSKU",
                "RZ09-05819EN4",
                "RZ09-05819EN9"),
            result.HostIdentityFailure);
        Assert.Equal(
            ExpectedRequests.Take(3).Select(request => request.Location),
            registry.Requests.Select(request => request.Location));
    }

    [Theory]
    [InlineData($"{BiosKey}|SystemProductName", "BIOS", "SystemProductName", "Blade 16 - RZ09-0581")]
    [InlineData($"{SystemInformationKey}|SystemManufacturer", "SystemInformation", "SystemManufacturer", "Razer")]
    public void ReadExactIdentity_ReportsExactMismatchedRegistryField(
        string location,
        string source,
        string field,
        string expectedValue)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[location] = "unexpected";

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        AssertIdentityBlocked(result);
        Assert.Equal(
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistryValueMismatch,
                source,
                field,
                expectedValue,
                "unexpected"),
            result.HostIdentityFailure);
    }

    [Theory]
    [MemberData(nameof(CaseDifferentRegistryValues))]
    public void ReadExactIdentity_UsesOrdinalCaseSensitiveComparison(string location, string caseDifferentValue)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[location] = caseDifferentValue;

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
    }

    [Theory]
    [MemberData(nameof(RegistryValueLocations))]
    public void ReadExactIdentity_BlocksMissingValues(string location)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values.Remove(location);

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
    }

    [Fact]
    public void ReadExactIdentity_AcceptsMissingProductionSkuAsNullMetadata()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values.Remove($"{BiosKey}|SystemSKU");

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        Assert.NotNull(result.Evidence);
        Assert.Null(result.Evidence.Value.Sku);
        Assert.Null(result.Failure);
        Assert.Null(result.HostIdentityFailure);
        Assert.Equal(
            ExpectedRequests.Select(request => request.Location),
            registry.Requests.Select(request => request.Location));
    }

    [Fact]
    public void ReadExactIdentity_AcceptsNonStringProductionSkuAsNullMetadata()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemSKU"] = new StringConvertibleRegistryValue("RZ09-05819EN4");

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        Assert.NotNull(result.Evidence);
        Assert.Null(result.Evidence.Value.Sku);
        Assert.Null(result.Failure);
        Assert.Equal(ExpectedRequests.Count(), registry.Requests.Count);
    }

    [Fact]
    public void ReadExactIdentity_AcceptsProductionSkuReadFailureAsNullMetadata()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.ThrowAt = $"{BiosKey}|SystemSKU";

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        Assert.NotNull(result.Evidence);
        Assert.Null(result.Evidence.Value.Sku);
        Assert.Null(result.Failure);
        Assert.Equal(ExpectedRequests.Count(), registry.Requests.Count);
    }

    [Fact]
    public void ReadExactIdentity_BoundsReportedRegistryValues()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemProductName"] = new string('x', 300);

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        HostIdentityFailureDetail failure = Assert.IsType<HostIdentityFailureDetail>(
            result.HostIdentityFailure);
        Assert.Equal(256, failure.ActualValue?.Length);
        Assert.Equal(new string('x', 256), failure.ActualValue);
    }

    [Theory]
    [MemberData(nameof(RegistryValueLocations))]
    public void ReadExactIdentity_BlocksWrongTypeValuesWithoutConvertingThem(string location)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[location] = new StringConvertibleRegistryValue((string)registry.Values[location]!);

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
    }

    [Theory]
    [MemberData(nameof(RegistryValueLocations))]
    public void ReadExactIdentity_BlocksAndContainsRegistryReadExceptions(string location)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.ThrowAt = location;

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
    }

    [Fact]
    public void ReadExactIdentity_ReportsRegistryExceptionTypeAndHResultOnly()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.ThrowAt = $"{BiosKey}|SystemManufacturer";

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        AssertIdentityBlocked(result);
        HostIdentityFailureDetail failure = Assert.IsType<HostIdentityFailureDetail>(
            result.HostIdentityFailure);
        Assert.Equal(HostIdentityFailureReason.RegistryReadFailed, failure.Reason);
        Assert.Equal("BIOS", failure.Source);
        Assert.Equal("SystemManufacturer", failure.Field);
        Assert.Equal(nameof(UnauthorizedAccessException), failure.ExceptionType);
        Assert.Equal(new UnauthorizedAccessException().HResult, failure.HResult);
        Assert.Null(failure.ExpectedValue);
        Assert.Null(failure.ActualValue);
    }

    [Theory]
    [MemberData(nameof(OverlappingRegistryValues))]
    public void ReadExactIdentity_BlocksCrossKeyDisagreement(string systemInformationLocation)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[systemInformationLocation] = $"{registry.Values[systemInformationLocation]} ";

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
    }

    [Fact]
    public void ReadExactIdentity_ReportsBiosVersionSourceDisagreement()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "4.01";

        HostIdentityReadResult result = CreateSource(registry).ReadExactIdentity();

        AssertIdentityBlocked(result);
        Assert.Equal(
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.RegistrySourcesMismatch,
                "BIOS/SystemInformation",
                "BIOSVersion",
                "3.01",
                "4.01"),
            result.HostIdentityFailure);
    }

    [Theory]
    [InlineData(10, 0, 19045)]
    [InlineData(6, 3, 9600)]
    [InlineData(11, 0, 26200)]
    public void ReadExactIdentity_BlocksNonWindows11Versions(int major, int minor, int build)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        FakeWindowsPlatformEvidenceSource platform = new(
            new WindowsPlatformEvidence(
                true,
                true,
                new Version(major, minor, build),
                Architecture.X64));

        AssertIdentityBlocked(new WindowsHostIdentitySource(registry, platform).ReadExactIdentity());
        Assert.Empty(registry.Requests);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Arm64)]
    public void ReadExactIdentity_BlocksNonX64Architectures(Architecture architecture)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        FakeWindowsPlatformEvidenceSource platform = new(
            ExactPlatform with { Architecture = architecture });

        AssertIdentityBlocked(new WindowsHostIdentitySource(registry, platform).ReadExactIdentity());
        Assert.Empty(registry.Requests);
    }

    [Fact]
    public void ReadExactIdentity_BlocksNonWindowsPlatform()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        FakeWindowsPlatformEvidenceSource platform = new(
            ExactPlatform with { IsWindows = false });

        HostIdentityReadResult result = new WindowsHostIdentitySource(
            registry,
            platform).ReadExactIdentity();

        AssertIdentityBlocked(result);
        Assert.Equal(
            new HostIdentityFailureDetail(
                HostIdentityFailureReason.PlatformNotSupported,
                "Platform",
                "IsWindows",
                bool.TrueString,
                bool.FalseString),
            result.HostIdentityFailure);
        Assert.Empty(registry.Requests);
    }

    [Fact]
    public void ReadExactIdentity_BlocksWindowsServerEvenAtAWindows11Build()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        FakeWindowsPlatformEvidenceSource platform = new(
            ExactPlatform with
            {
                IsWorkstation = false,
                Version = new Version(10, 0, 26100),
            });

        AssertIdentityBlocked(new WindowsHostIdentitySource(registry, platform).ReadExactIdentity());
        Assert.Empty(registry.Requests);
    }

    [Theory]
    [InlineData("Client", true)]
    [InlineData("Server", false)]
    [InlineData("Server Core", false)]
    [InlineData("client", false)]
    public void RuntimePlatformEvidence_RecognizesOnlyExactClientInstallationType(
        string installationType,
        bool expectedWorkstation)
    {
        FakeRegistryValueSource registry = new();
        registry.Values[$"{CurrentVersionKey}|InstallationType"] = installationType;
        RuntimeWindowsPlatformEvidenceSource source = new(registry);

        WindowsPlatformEvidence evidence = source.Read();

        Assert.Equal(expectedWorkstation, evidence.IsWorkstation);
        ObservedRegistryRequest request = Assert.Single(registry.Requests);
        Assert.Equal(RegistryHive.LocalMachine, request.Hive);
        Assert.Equal(RegistryView.Registry64, request.View);
        Assert.Equal($"{CurrentVersionKey}|InstallationType", request.Location);
    }

    [Fact]
    public void ReadExactIdentity_BlocksAndContainsPlatformEvidenceSourceFailure()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        FakeWindowsPlatformEvidenceSource platform = new(ExactPlatform)
        {
            Throw = true,
        };

        HostIdentityReadResult result = new WindowsHostIdentitySource(
            registry,
            platform).ReadExactIdentity();

        AssertIdentityBlocked(result);
        HostIdentityFailureDetail failure = Assert.IsType<HostIdentityFailureDetail>(
            result.HostIdentityFailure);
        Assert.Equal(HostIdentityFailureReason.PlatformReadFailed, failure.Reason);
        Assert.Equal("Platform", failure.Source);
        Assert.Equal("Evidence", failure.Field);
        Assert.Equal(nameof(InvalidOperationException), failure.ExceptionType);
        Assert.Equal(new InvalidOperationException().HResult, failure.HResult);
        Assert.Empty(registry.Requests);
    }

    private static TheoryData<string, string> CreateRequestData(
        Func<RegistryValueRequest, string> valueFactory)
    {
        TheoryData<string, string> data = new();
        foreach (RegistryValueRequest request in RequiredIdentityRequests)
        {
            data.Add(request.Location, valueFactory(request));
        }

        return data;
    }

    private static FakeRegistryValueSource CreateExactRegistry()
    {
        FakeRegistryValueSource registry = new();
        foreach (RegistryValueRequest request in ExpectedRequests)
        {
            registry.Values.Add(request.Location, request.ExpectedValue);
        }

        return registry;
    }

    private static IEnumerable<RegistryValueRequest> RequiredIdentityRequests =>
        ExpectedRequests.Where(request => request.ValueName != "SystemSKU");

    private static WindowsHostIdentitySource CreateSource(FakeRegistryValueSource registry) =>
        new(registry, new FakeWindowsPlatformEvidenceSource(ExactPlatform));

    private static void AssertIdentityBlocked(HostIdentityReadResult result)
    {
        Assert.Null(result.Evidence);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
    }

    private sealed class FakeRegistryValueSource : IRegistryValueSource
    {
        public Dictionary<string, object?> Values { get; } = [];

        public List<ObservedRegistryRequest> Requests { get; } = [];

        public string? ThrowAt { get; set; }

        public object? Read(RegistryHive hive, RegistryView view, string key, string valueName)
        {
            Assert.Equal(RegistryHive.LocalMachine, hive);
            Assert.Equal(RegistryView.Registry64, view);

            ObservedRegistryRequest request = new(hive, view, $"{key}|{valueName}");
            Requests.Add(request);
            if (request.Location == ThrowAt)
            {
                throw new UnauthorizedAccessException("inaccessible test value");
            }

            return Values.GetValueOrDefault(request.Location);
        }
    }

    private sealed class StringConvertibleRegistryValue(string text)
    {
        public override string ToString() => text;
    }

    private sealed class FakeWindowsPlatformEvidenceSource(WindowsPlatformEvidence evidence)
        : IWindowsPlatformEvidenceSource
    {
        public bool Throw { get; set; }

        public WindowsPlatformEvidence Read()
        {
            if (Throw)
            {
                throw new InvalidOperationException("platform evidence unavailable");
            }

            return evidence;
        }
    }

    private readonly record struct RegistryValueRequest(
        string Key, string ValueName, string ExpectedValue)
    {
        public string Location => $"{Key}|{ValueName}";
    }

    private readonly record struct ObservedRegistryRequest(
        RegistryHive Hive, RegistryView View, string Location);
}
