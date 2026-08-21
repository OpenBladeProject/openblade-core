using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class WindowsFirmwareVersionSourceTests
{
    [Fact]
    public void Capture_ReadsSafeWindowsFirmwareMetadataAndNormalizesVersions()
    {
        var values = new Dictionary<string, object?>
        {
            ["BIOSVersion"] = " 3.01 ",
            ["ECFirmwareMajorRelease"] = 1,
            ["ECFirmwareMinorRelease"] = 4,
            ["SystemSKU"] = "RZ09-05819EN4",
        };
        var source = new WindowsFirmwareVersionSource(
            (_, name) => values.GetValueOrDefault(name),
            () =>
            [
                new VideoFirmwareCandidate("Intel Graphics", "Intel Video BIOS"),
                new VideoFirmwareCandidate(
                    "NVIDIA GeForce RTX 5090 Laptop GPU",
                    "Version98.3.5c.40.3d"),
            ],
            () => "69.1",
            () => "1.07");

        FirmwareVersions result = source.Capture();

        Assert.Equal("3.01", result.BiosVersion);
        Assert.Equal("1.04", result.EcVersion);
        Assert.Equal("Unavailable", result.McuVersion);
        Assert.Equal("98.3.5c.40.3d", result.VbiosVersion);
        Assert.Equal("NVIDIA GeForce RTX 5090 Laptop GPU", result.VbiosGpuName);
        Assert.Equal("RZ09-0581", result.ModelNumber);
        Assert.Equal("69.1", result.ThunderboltFirmwareVersion);
        Assert.Equal("1.07", result.TouchpadFirmwareVersion);
        Assert.Equal(
            DeviceFeatureAccess.ReadOnly,
            result.ThunderboltFirmwareAccess);
        Assert.Equal(DeviceFeatureAccess.ReadOnly, result.TouchpadFirmwareAccess);
    }

    [Fact]
    public void Capture_DegradesFirmwareReaderFailuresToUnavailableButKeepsReadOnlyCapability()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name switch
            {
                "SystemSKU" => "RZ09-05819EN4",
                "BIOSVersion" => "4.00",
                "ECFirmwareMajorRelease" => 1,
                "ECFirmwareMinorRelease" => 4,
                _ => null,
            },
            () => [],
            () => throw new System.ComponentModel.Win32Exception(5),
            () => "not-a-version");

        FirmwareVersions result = source.Capture();

        Assert.Equal("Unavailable", result.ThunderboltFirmwareVersion);
        Assert.Equal("Unavailable", result.TouchpadFirmwareVersion);
        Assert.Equal(DeviceFeatureAccess.ReadOnly, result.ThunderboltFirmwareAccess);
        Assert.Equal(DeviceFeatureAccess.ReadOnly, result.TouchpadFirmwareAccess);
    }

    [Theory]
    [InlineData("3.01", 1, 4)]
    [InlineData("4.00", 1, 3)]
    [InlineData("99.99", 9, 9)]
    [InlineData(null, 1, 4)]
    public void Capture_ReadsComponentsAcrossModelScopedFirmware(
        string? biosVersion,
        int ecMajor,
        int ecMinor)
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name switch
            {
                "SystemSKU" => "RZ09-05819EN4",
                "BIOSVersion" => biosVersion,
                "ECFirmwareMajorRelease" => ecMajor,
                "ECFirmwareMinorRelease" => ecMinor,
                _ => null,
            },
            () => [],
            () => "69.1",
            () => "1.07");

        FirmwareVersions result = source.Capture();

        Assert.Equal("69.1", result.ThunderboltFirmwareVersion);
        Assert.Equal("1.07", result.TouchpadFirmwareVersion);
        Assert.Equal(DeviceFeatureAccess.ReadOnly, result.ThunderboltFirmwareAccess);
        Assert.Equal(DeviceFeatureAccess.ReadOnly, result.TouchpadFirmwareAccess);
    }

    [Fact]
    public void Capture_DoesNotInvokeExactDeviceReadersForAnotherModel()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name == "SystemSKU" ? "RZ09-0528ABC" : null,
            () => [],
            () => throw new InvalidOperationException("must not read Thunderbolt"),
            () => throw new InvalidOperationException("must not read touchpad"));

        FirmwareVersions result = source.Capture();

        Assert.Null(result.ThunderboltFirmwareAccess);
        Assert.Null(result.TouchpadFirmwareAccess);
        Assert.Equal("Unavailable", result.ThunderboltFirmwareVersion);
        Assert.Equal("Unavailable", result.TouchpadFirmwareVersion);
    }

    [Fact]
    public void Capture_DegradesEachMissingOrInaccessibleSourceToUnavailable()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, _) => throw new UnauthorizedAccessException("denied"),
            () => throw new IOException("unavailable"));

        Assert.Equal(FirmwareVersions.Unavailable(), source.Capture());
    }

    [Fact]
    public void Capture_RejectsDistinctNvidiaFirmwareCandidatesAsAmbiguous()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, _) => null,
            () =>
            [
                new VideoFirmwareCandidate(
                    "NVIDIA GeForce RTX 5080 Laptop GPU",
                    "Version98.03.5C.40.26"),
                new VideoFirmwareCandidate(
                    "NVIDIA GeForce RTX 5090 Laptop GPU (stale eGPU registry entry)",
                    "Version98.03.5C.40.25"),
            ]);

        FirmwareVersions result = source.Capture();

        Assert.Equal("Unavailable", result.VbiosVersion);
        Assert.Equal("Unavailable", result.VbiosGpuName);
    }

    [Fact]
    public void Capture_RejectsDuplicateNvidiaCandidatesAsAmbiguous()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, _) => null,
            () =>
            [
                new VideoFirmwareCandidate(
                    "NVIDIA GeForce RTX 5090 Laptop GPU",
                    "Version98.03.5C.40.3D"),
                new VideoFirmwareCandidate(
                    "NVIDIA GeForce RTX 5090 Laptop GPU",
                    "Version98.03.5C.40.3D"),
            ]);

        FirmwareVersions result = source.Capture();

        Assert.Equal("Unavailable", result.VbiosVersion);
        Assert.Equal("Unavailable", result.VbiosGpuName);
    }

    [Fact]
    public void RegistryAdapterInstances_CollapseIdenticalDisplayHeadAliases()
    {
        VideoFirmwareCandidate[] aliases =
        [
            new("NVIDIA GeForce RTX 5090 Laptop GPU", "Version98.03.5C.40.3D"),
            new("NVIDIA GeForce RTX 5090 Laptop GPU", "Version98.03.5C.40.3D"),
            new(" NVIDIA GeForce RTX 5090 Laptop GPU ", " Version98.03.5C.40.3D "),
        ];

        VideoFirmwareCandidate candidate = Assert.Single(
            WindowsFirmwareVersionSource.CollapseRegistryAdapterInstances(aliases));

        Assert.Equal("NVIDIA GeForce RTX 5090 Laptop GPU", candidate.AdapterName);
        Assert.Equal("Version98.03.5C.40.3D", candidate.BiosVersion);
    }

    [Fact]
    public void RegistryAdapterInstances_PreserveConflictingCandidatesForFailClosedSelection()
    {
        VideoFirmwareCandidate[] conflicting =
        [
            new("NVIDIA GeForce RTX 5080 Laptop GPU", "Version98.03.5C.40.16"),
            new("NVIDIA GeForce RTX 5090 Laptop GPU", "Version98.03.5C.40.15"),
        ];

        Assert.Equal(
            2,
            WindowsFirmwareVersionSource.CollapseRegistryAdapterInstances(conflicting).Count);
    }

    [Fact]
    public void Capture_LeavesVbiosUnavailableWithoutAnNvidiaFirmwareCandidate()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, _) => null,
            () => [new VideoFirmwareCandidate("Intel Graphics", "Intel Video BIOS")]);

        FirmwareVersions result = source.Capture();

        Assert.Equal("Unavailable", result.VbiosVersion);
        Assert.Equal("Unavailable", result.VbiosGpuName);
    }

    [Fact]
    public void Capture_DoesNotPresentMissingEcComponentsAsZeroVersion()
    {
        var source = new WindowsFirmwareVersionSource((_, _) => null, () => []);

        Assert.Equal("Unavailable", source.Capture().EcVersion);
    }

    [Fact]
    public void Capture_PreservesAnUnknownModelForCompatibilityReporting()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name == "SystemSKU" ? "RZ09-9999ABC" : null,
            () => []);

        Assert.Equal("RZ09-9999ABC", source.Capture().ModelNumber);
    }

    [Fact]
    public void Capture_DoesNotExposeRz090581PublishedOnlyComponentsOnRz090528()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name == "SystemSKU" ? "RZ09-0528ABC" : null,
            () => []);

        FirmwareVersions result = source.Capture();

        Assert.Null(result.ThunderboltFirmwareAccess);
        Assert.Null(result.TouchpadFirmwareAccess);
        Assert.Equal("Unavailable", result.ThunderboltFirmwareVersion);
        Assert.Equal("Unavailable", result.TouchpadFirmwareVersion);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData("not-a-number")]
    public void Capture_RejectsInvalidEcComponents(object invalidMinor)
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name switch
            {
                "BIOSVersion" => "3.01",
                "ECFirmwareMajorRelease" => 1,
                "ECFirmwareMinorRelease" => invalidMinor,
                _ => null,
            },
            () => []);

        Assert.Equal("Unavailable", source.Capture().EcVersion);
    }

    [Fact]
    public void ThunderboltSelector_RequiresOneExactRootRouterWithComparableVersion()
    {
        WindowsPnpFirmwareCandidate[] candidates =
        [
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_E333\integrated",
                "11.7",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_E333&REV_0000"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_E333&SUBSYS_30101A58&REV_00&USB4_MS_CM\root"),
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\thunderbolt",
                " 69.1 ",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM\root"),
        ];

        Assert.Equal(
            "69.1",
            WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(candidates));
    }

    [Fact]
    public void ThunderboltSelector_RejectsMissingAmbiguousAndMalformedCandidates()
    {
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion([]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(
        [
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\first",
                "69.1",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM\first"),
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\second",
                "69.1",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM\second"),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(
        [
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\only",
                "invalid",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM\root"),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(
        [
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\wrong-revision",
                "69.1",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0000"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM\root"),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(
        [
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\wrong-service",
                "69.1",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084"],
                "WrongService",
                @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM\root"),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(
        [
            new(
                @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781\wrong-parent",
                "69.1",
                [@"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084"],
                "Usb4DeviceRouter",
                @"PCI\VEN_8086&DEV_E333&SUBSYS_30101A58&REV_00&USB4_MS_CM\root"),
        ]));
    }

    [Fact]
    public void TouchpadSelector_FormatsExactPrecisionTouchpadBcdVersion()
    {
        WindowsHidFirmwareCandidate[] candidates =
        [
            new(0x093A, 0x3001, 0x000D, 0x0005, 0x0107),
            new(0x093A, 0x3001, 0x0001, 0x0002, 0x0107),
        ];

        Assert.Equal(
            "1.07",
            WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(candidates));
        Assert.True(WindowsDeviceFirmwareVersionReader.TryFormatBcdVersion(
            0x1234,
            out string? formatted));
        Assert.Equal("12.34", formatted);
    }

    [Fact]
    public void TouchpadSelector_RejectsWrongMissingAmbiguousAndInvalidCandidates()
    {
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion([]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
        [
            new(0x093B, 0x3001, 0x000D, 0x0005, 0x0107),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
        [
            new(0x093A, 0x3002, 0x000D, 0x0005, 0x0107),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
        [
            new(0x093A, 0x3001, 0x0001, 0x0005, 0x0107),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
        [
            new(0x093A, 0x3001, 0x000D, 0x0006, 0x0107),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
        [
            new(0x093A, 0x3001, 0x000D, 0x0005, 0x0107),
            new(0x093A, 0x3001, 0x000D, 0x0005, 0x0107),
        ]));
        Assert.Null(WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
        [
            new(0x093A, 0x3001, 0x000D, 0x0005, 0x0000),
        ]));
        Assert.False(WindowsDeviceFirmwareVersionReader.TryFormatBcdVersion(
            0x01A7,
            out _));
    }
}
