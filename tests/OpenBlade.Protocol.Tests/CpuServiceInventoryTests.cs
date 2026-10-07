using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CpuServiceInventoryTests
{
    [Theory]
    [InlineData(0x10, false, 0x20, "Getter service runtime type changed.")]
    [InlineData(0x20, true, 0x20, "Getter service display name changed.")]
    [InlineData(0x20, false, 0x10, "Getter service configured/runtime incompatible.")]
    [InlineData(0x10, true, 0x10, "Getter service runtime type changed.")]
    [InlineData(0x20, true, 0x10, "Getter service display name changed.")]
    public void GetterIdentityRejectionsPreservePrecedenceAndStopBeforeConfigurationReads(
        int runtime, bool displayChanged, int configured, string reason)
    {
        var platform = new Fake { Type = 0x20 };
        var inventory = new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform);
        var prior = Assert.Single(inventory.ReadAll());
        platform.RuntimeType = (uint)runtime;
        platform.DisplayName = displayChanged ? "Changed private display" : prior.DisplayName;
        var exception = Assert.Throws<IOException>(() => inventory.ReadGetterActivity(
            [prior with { ConfiguredType = (uint)configured }]));
        Assert.Equal(reason, exception.Message);
        Assert.Equal(2, platform.Calls); Assert.Equal(1, platform.RegistryReads);
        Assert.Equal(0, platform.ConfigReads); Assert.Equal(0, platform.TemplateReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("config-drift")]
    [InlineData("partial")]
    [InlineData("type-drift")]
    public void GetterActivityUsesBoundedScmAndChecksRelevantConfiguration(string? drift)
    {
        var platform = new Fake { Type = 0x20 };
        var inventory = new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform);
        var baseline = inventory.ReadAll();
        int registry = platform.RegistryReads;
        platform.Fault = drift ?? string.Empty;
        if (drift == "type-drift") { platform.RuntimeType = 0x10; }
        if (drift is null) { Assert.Single(inventory.ReadGetterActivity(baseline)); }
        else { Assert.Throws<IOException>(() => inventory.ReadGetterActivity(baseline)); }
        Assert.Equal(registry, platform.RegistryReads);
    }

    [Fact]
    public void GetterActivityStillChecksOmittedTemplateAndRejectsNewInventory()
    {
        var platform = new Fake { Type = 0x20, Fault = "unmatched-template" };
        var inventory = new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform);
        var baseline = inventory.ReadAll();
        Assert.Equal(2, inventory.ReadGetterActivity(baseline).Count);
        platform.Fault = "template-active";
        Assert.Throws<IOException>(() => inventory.ReadGetterActivity(baseline));
        platform.Fault = string.Empty;
        Assert.Throws<IOException>(() => inventory.ReadGetterActivity([]));
    }

    [Fact]
    public void RegistryOnlyRecognizerRequiresAbsentImageAndExactScmAbsence()
    {
        Assert.Equal(CpuServiceRegistryDisposition.RegistryOnlyRecognizer,
            CpuServiceInventory.ClassifyRecognizer(8, CpuServiceInventory.DecodeRegistryValue(2, 0, [], 0, true), () => 1060));
        Assert.Equal(CpuServiceRegistryDisposition.RequireScmEntry,
            CpuServiceInventory.ClassifyRecognizer(8, null, () => 0));
        Assert.Throws<IOException>(() => CpuServiceInventory.ClassifyRecognizer(8, null, () => 5));
        Assert.Throws<IOException>(() => CpuServiceInventory.ClassifyRecognizer(8, null, () => throw new IOException()));
        Assert.Throws<IOException>(() => CpuServiceInventory.ClassifyRecognizer(8,
            CpuServiceInventory.DecodeRegistryValue(0, 0, [], 0, true), () => 1060));
    }
    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(4, null)]
    [InlineData(8, "")]
    [InlineData(8, "System32\\drivers\\recognizer.sys")]
    public void OtherDriversAndPresentRecognizerImagesCannotUseExclusion(int type, string? image)
    {
        Assert.Equal(CpuServiceRegistryDisposition.RequireScmEntry,
            CpuServiceInventory.ClassifyRecognizer((uint)type, image, () => throw new InvalidOperationException()));
    }
    [Fact]
    public void RecognizerReturnedByNativeEnumerationCannotBeExcludedLater()
    {
        var platform = new Fake { Type = 8, Fault = "missing-registry" };
        Assert.Throws<IOException>(() => new CpuServiceInventory(CpuServiceInventoryKind.Drivers, platform).ReadAll());
    }
    [Fact]
    public void OnlyNativeMissingValueStatusCanEstablishRegistryAbsence()
    {
        Assert.Null(CpuServiceInventory.DecodeRegistryValue(2, 0, [], 0, true));
        Assert.Null(CpuServiceInventory.DecodeRegistryValue(2, 0, [], 0, false));
        Assert.Equal(1, CpuServiceInventory.DecodeRegistryValue(0, 4, [1, 0, 0, 0], 4, false));
        Assert.Equal("", CpuServiceInventory.DecodeRegistryValue(0, 1, [0, 0], 2, true));
        Assert.Equal("%SystemRoot%\\a.sys", CpuServiceInventory.DecodeRegistryValue(0, 2,
            Encoding.Unicode.GetBytes("%SystemRoot%\\a.sys\0"), 38, true));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    public void PresentUnsupportedKindsNeverBecomeMissing(int kind)
    {
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(0, (uint)kind, [0, 0], 2, true));
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(0, (uint)kind, [0, 0, 0, 0], 4, false));
    }
    [Fact]
    public void ErrorsTruncationAndMalformedStringsNeverEnableDefaultPath()
    {
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(234, 1, [0, 0], 4, true));
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(5, 0, [], 0, true));
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(0, 1, [65, 0], 2, true));
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(0, 1, [0, 0, 0, 0], 4, true));
        Assert.Throws<IOException>(() => CpuServiceInventory.DecodeRegistryValue(0, 1, [0, 0xD8, 0, 0], 4, true));
    }
    [Fact]
    public void ExactRuntimeRegistryAgreementNeedsNoAdditionalConfigRead()
    {
        var platform = new Fake { Type = 0x20 };
        var result = Assert.Single(new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform).ReadAll());
        Assert.Equal(result.Type, result.ConfiguredType);
        Assert.Equal(0, platform.ConfigReads);
    }
    [Theory]
    [InlineData(0x20, 0x30)]
    [InlineData(0xE0, 0xF0)]
    public void SplitSharedServicePreservesRawRuntimeAndIndependentlyCheckedConfiguration(int configured, int runtime)
    {
        var platform = new Fake { Type = (uint)configured, RuntimeType = (uint)runtime };
        var result = Assert.Single(new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform).ReadAll());
        Assert.Equal((uint)runtime, result.Type); Assert.Equal((uint)configured, result.ConfiguredType);
        Assert.Equal(1, platform.ConfigReads);
        platform.Fault = "config-drift";
        Assert.Throws<IOException>(() => new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform).ReadAll());
    }
    [Theory]
    [InlineData(0x10, 0x30)]
    [InlineData(0x20, 0x10)]
    [InlineData(0xE0, 0x30)]
    [InlineData(0x20, 0xF0)]
    [InlineData(0x20, 0x130)]
    public void SplitRelationCannotAddRemoveOrMaskOtherFlags(int configured, int runtime) =>
        Assert.False(CpuServiceInventory.TypesAgree(CpuServiceInventoryKind.Win32, (uint)configured, (uint)runtime));
    [Fact]
    public void DocumentedDefaultRequiresAbsentRegistryAndEmptyExactScmConfig()
    {
        Assert.Equal(@"C:\Windows\System32\drivers\AMDRyzenMasterDriverRenamed.sys",
            CpuServiceInventory.DefaultDriverPath("AMDRyzenMasterDriverRenamed", 1, true, "", @"C:\Windows"));
        Assert.Equal(@"C:\Windows\System32\drivers\Fs_Rec.sys",
            CpuServiceInventory.DefaultDriverPath("Fs_Rec", 8, true, "", @"C:\Windows"));
        Assert.Throws<IOException>(() => CpuServiceInventory.DefaultDriverPath("Beep", 1, false, "", @"C:\Windows"));
        Assert.Throws<IOException>(() => CpuServiceInventory.DefaultDriverPath("Beep", 1, true, "configured.sys", @"C:\Windows"));
        Assert.Throws<IOException>(() => CpuServiceInventory.DefaultDriverPath("Beep", 4, true, "", @"C:\Windows"));
    }
    [Theory]
    [InlineData("../Beep")]
    [InlineData("Beep:stream")]
    [InlineData("Beep.")]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("beep test")]
    public void DefaultDriverNameNeverEscapesPlainFileName(string name) => Assert.Throws<IOException>(() =>
        CpuServiceInventory.DefaultDriverPath(name, 1, true, "", @"C:\Windows"));
    [Fact]
    public void MissingRegistryDriverImageUsesActualBoundedScmConfigPath()
    {
        var bytes = DriverConfig();
        Assert.Equal(@"System32\drivers\actual.sys", CpuServiceInventory.ParseDriverImage(bytes, 0x1000, 1));
    }
    [Theory]
    [InlineData("type")]
    [InlineData("pointer")]
    [InlineData("header-pointer")]
    [InlineData("unaligned")]
    [InlineData("unterminated")]
    [InlineData("empty")]
    [InlineData("unicode")]
    public void MissingImageFallbackNeverAcceptsMalformedConfig(string failure)
    {
        var bytes = DriverConfig();
        switch (failure)
        {
            case "type": BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2); break;
            case "pointer": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0x3000); break;
            case "header-pointer": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0x1010); break;
            case "unaligned": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0x1041); break;
            case "unterminated": bytes.AsSpan(64).Fill(65); break;
            case "empty": bytes[64] = bytes[65] = 0; break;
            case "unicode": bytes[64] = 0; bytes[65] = 0xD8; bytes[66] = bytes[67] = 0; break;
        }
        Assert.Throws<IOException>(() => CpuServiceInventory.ParseDriverImage(bytes, 0x1000, 1));
    }
    private static byte[] DriverConfig()
    {
        byte[] bytes = new byte[8192]; Array.Fill(bytes, (byte)0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0x1040);
        Encoding.Unicode.GetBytes("System32\\drivers\\actual.sys\0").CopyTo(bytes, 64);
        return bytes;
    }
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x20)]
    [InlineData(0x110)]
    [InlineData(0x120)]
    [InlineData(0x50)]
    [InlineData(0x60)]
    [InlineData(0xD0)]
    [InlineData(0xE0)]
    [InlineData(0x210)]
    [InlineData(0x220)]
    public void ExactOrdinaryInteractiveAndPerUserFlagsArePreserved(int type)
    {
        var platform = new Fake { Type = (uint)type };
        var result = new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform).ReadAll();
        Assert.Equal((uint)type, Assert.Single(result).Type); Assert.Equal("Razer controller", result[0].DisplayName);
        Assert.Equal(1, platform.Calls); Assert.Equal(56, Marshal.SizeOf<CpuServiceInventory.NativeEntry>());
        Assert.Equal(16, Marshal.OffsetOf<CpuServiceInventory.NativeEntry>("Type").ToInt32());
    }
    [Theory]
    [InlineData("partial")]
    [InlineData("error")]
    [InlineData("resume")]
    [InlineData("remaining")]
    [InlineData("count")]
    [InlineData("pointer")]
    [InlineData("display-pointer")]
    [InlineData("duplicate")]
    [InlineData("missing-registry")]
    [InlineData("hidden-native")]
    [InlineData("type-drift")]
    [InlineData("unknown-type")]
    [InlineData("state")]
    [InlineData("registry-error")]
    [InlineData("template-active")]
    [InlineData("template-type-drift")]
    [InlineData("template-error")]
    public void PartialHiddenMalformedOrUnknownInventoryNeverCompletes(string fault)
    {
        var platform = new Fake { Fault = fault };
        Assert.Throws<IOException>(() => new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform).ReadAll());
        Assert.Equal(1, platform.Calls);
    }
    [Fact]
    public void OmittedTemplateRequiresIndependentExactStoppedStatus()
    {
        var platform = new Fake { Fault = "unmatched-template" };
        var result = new CpuServiceInventory(CpuServiceInventoryKind.Win32, platform).ReadAll();
        Assert.Equal(2, result.Count); Assert.Equal(1, platform.TemplateReads);
        Assert.Equal(0x50u, result[1].Type); Assert.Equal(1u, result[1].State);
    }
    [Theory]
    [InlineData(5)]
    [InlineData(234)]
    [InlineData(87)]
    public void RegistryCompletionRequiresTerminalNoMoreItems(int failure)
    {
        Assert.Throws<IOException>(() => CpuServiceInventory.ReadRegistryNames(index => index == 0
            ? new(0, "Other", 5) : new(failure, "", 0)));
        Assert.Equal(["Other"], CpuServiceInventory.ReadRegistryNames(index => index == 0
            ? new(0, "Other", 5) : new(259, "", 0)));
    }
    [Fact]
    public void MissingTypeRequiresExactNamedServiceAbsenceAndCannotHideManagerFailure()
    {
        Assert.True(CpuServiceInventory.ExcludeNonServiceKey(null, () => 1060));
        Assert.False(CpuServiceInventory.ExcludeNonServiceKey(0x10, () => throw new IOException()));
        Assert.Throws<IOException>(() => CpuServiceInventory.ExcludeNonServiceKey(null, () => 0));
        Assert.Throws<IOException>(() => CpuServiceInventory.ExcludeNonServiceKey(null, () => 5));
        Assert.Throws<IOException>(() => CpuServiceInventory.ExcludeNonServiceKey(null, () => throw new IOException()));
        Assert.Throws<IOException>(() => CpuServiceInventory.ExcludeNonServiceKey("16", () => 1060));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(0x30)]
    [InlineData(0x90)]
    [InlineData(0x150)]
    [InlineData(0x800000)]
    public void UnknownOrMixedFlagsCannotBeMaskedToKnownType(int type) => Assert.False(CpuServiceInventory.KnownType((uint)type));
    private sealed class Fake : ICpuServiceInventoryPlatform
    {
        internal uint Type = 0x10;
        internal uint? RuntimeType;
        internal string DisplayName = "Razer controller";
        internal string Fault = "";
        internal int Calls;
        internal int ConfigReads;
        public uint ReadConfiguredType(string name)
        { ConfigReads++; return Fault == "config-drift" ? 0x10u : name == "Template" ? 0x50u : Type; }
        internal int TemplateReads;
        public CpuServiceTemplateStatus ReadUnenumeratedTemplateStatus(string name)
        {
            TemplateReads++; Assert.Equal("Template", name);
            return Fault switch
            {
                "template-error" => throw new IOException(),
                "template-type-drift" => new(0xD0, 1),
                "template-active" => new(0x50, 4),
                _ => new(0x50, 1)
            };
        }
        public CpuServiceEnumeration Enumerate(nint buffer, int bytes)
        {
            Calls++; Assert.Equal(CpuServiceInventory.BufferBytes, bytes);
            nint name = buffer + 112, display = buffer + 144;
            byte[] nameBytes = Encoding.Unicode.GetBytes("Other\0"), displayBytes = Encoding.Unicode.GetBytes(DisplayName + "\0");
            Marshal.Copy(nameBytes, 0, name, nameBytes.Length); Marshal.Copy(displayBytes, 0, display, displayBytes.Length);
            var entry = new CpuServiceInventory.NativeEntry
            {
                Name = Fault == "pointer" ? buffer - 1 : name,
                DisplayName = Fault == "display-pointer" ? buffer + bytes : display,
                Type = Fault == "unknown-type" ? 0x800000 : RuntimeType ?? Type,
                State = Fault == "state" ? 0u : 1u
            };
            Marshal.StructureToPtr(entry, buffer, false);
            if (Fault == "duplicate") { Marshal.StructureToPtr(entry, buffer + 56, false); }
            return Fault switch
            {
                "partial" => new(false, 234, 56, 1, 1),
                "error" => new(false, 5, 0, 1, 0),
                "resume" => new(true, 0, 0, 1, 1),
                "remaining" => new(true, 0, 56, 1, 0),
                "count" => new(true, 0, 0, 4097, 0),
                "duplicate" => new(true, 0, 0, 2, 0),
                _ => new(true, 0, 0, 1, 0)
            };
        }
        internal int RegistryReads;
        public IReadOnlyList<CpuServiceRegistryEntry> ReadRegistryEntries()
        {
            RegistryReads++;
            return Fault switch
            {
                "registry-error" => throw new IOException(),
                "missing-registry" => [],
                "hidden-native" => [new("Other", Type, ""), new("Hidden", 0x10, "")],
                "unmatched-template" or "template-error" or "template-type-drift" or "template-active" => [new("Other", Type, ""), new("Template", 0x50, "")],
                "type-drift" => [new("Other", 0x20, "")],
                _ => [new("Other", Type, "")]
            };
        }
    }
}
