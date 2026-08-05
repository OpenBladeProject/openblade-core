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
            foreach (RegistryValueRequest request in ExpectedRequests)
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
        foreach (RegistryValueRequest request in ExpectedRequests)
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
        Assert.Equal(
            ExpectedRequests.Select(request => request.Location).Order(),
            registry.Requests.Select(request => request.Location).Order());
        Assert.All(registry.Requests, request =>
        {
            Assert.Equal(RegistryHive.LocalMachine, request.Hive);
            Assert.Equal(RegistryView.Registry64, request.View);
        });
    }

    [Fact]
    public void ReadExactIdentity_AcceptsExplicitRz090528Identity()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|SystemProductName"] = "Blade 16 - RZ09-0528";
        registry.Values[$"{BiosKey}|SystemSKU"] = "RZ09-05289EN4";
        registry.Values[$"{BiosKey}|BIOSVersion"] = "2.02";
        registry.Values[$"{SystemInformationKey}|SystemProductName"] =
            "Blade 16 - RZ09-0528";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "2.02";
        var source = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            new ExpectedHostIdentity(
                "Razer",
                "Blade 16 - RZ09-0528",
                "RZ09-05289EN4",
                "2.02"));

        HostIdentityReadResult result = source.ReadExactIdentity();

        Assert.Equal(
            new HostIdentityEvidence(
                "Razer",
                "Blade 16 - RZ09-0528",
                "RZ09-05289EN4",
                "2.02"),
            result.Evidence);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void ReadExactIdentity_UntestedFirmwareRequiresExplicitPolicy()
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[$"{BiosKey}|BIOSVersion"] = "3.02";
        registry.Values[$"{SystemInformationKey}|BIOSVersion"] = "3.02";
        var policy = new FirmwareCompatibilityPolicy();
        var source = new WindowsHostIdentitySource(
            registry,
            new FakeWindowsPlatformEvidenceSource(ExactPlatform),
            policy);

        AssertIdentityBlocked(source.ReadExactIdentity());

        policy.AllowUntestedFirmware = true;
        HostIdentityReadResult result = source.ReadExactIdentity();

        Assert.Equal("3.02", result.Evidence?.BiosVersion);
        Assert.Null(result.Failure);
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

    [Theory]
    [MemberData(nameof(OverlappingRegistryValues))]
    public void ReadExactIdentity_BlocksCrossKeyDisagreement(string systemInformationLocation)
    {
        FakeRegistryValueSource registry = CreateExactRegistry();
        registry.Values[systemInformationLocation] = $"{registry.Values[systemInformationLocation]} ";

        AssertIdentityBlocked(CreateSource(registry).ReadExactIdentity());
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

        AssertIdentityBlocked(new WindowsHostIdentitySource(registry, platform).ReadExactIdentity());
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

        AssertIdentityBlocked(new WindowsHostIdentitySource(registry, platform).ReadExactIdentity());
        Assert.Empty(registry.Requests);
    }

    private static TheoryData<string, string> CreateRequestData(
        Func<RegistryValueRequest, string> valueFactory)
    {
        TheoryData<string, string> data = new();
        foreach (RegistryValueRequest request in ExpectedRequests)
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
