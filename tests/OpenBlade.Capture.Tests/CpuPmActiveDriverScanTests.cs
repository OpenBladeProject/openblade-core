using System.Runtime.InteropServices;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmActiveDriverScanTests
{
    [Fact]
    public void DocumentedDefaultProvenanceSurvivesAdapterAndAmdNameStillBlocksAbsence()
    {
        var record = Assert.Single(new WindowsCpuPmDeviceDriverSource(new Inventory { Fault = "default-amd" }).ReadAll());
        Assert.Equal(OpenBlade.Protocol.CpuServiceImageProvenance.DefaultDriverPath, record.ImageProvenance);
        var result = CpuPmActiveDriverScan.Read(new Source([record]));
        Assert.True(result.Complete); Assert.Equal(1, result.MatchingActiveDrivers);
    }
    [Theory]
    [InlineData(5)]
    [InlineData(234)]
    [InlineData(87)]
    public void RegistryEnumerationRequiresExplicitNoMoreItemsAfterEveryName(int error)
    {
        Assert.Throws<IOException>(() => WindowsCpuPmDeviceDriverSource.ReadRegistryNames(index =>
            index == 0 ? new(0, "Other", 5) : new(error, "", 0)));
        var names = WindowsCpuPmDeviceDriverSource.ReadRegistryNames(index =>
            index == 0 ? new(0, "Other", 5) : new(259, "", 0));
        Assert.Equal(["Other"], names);
    }
    [Fact]
    public void RegistryDuplicateMalformedLengthAndBoundNeverComplete()
    {
        Assert.Throws<IOException>(() => WindowsCpuPmDeviceDriverSource.ReadRegistryNames(_ => new(0, "Other", 5)));
        Assert.Throws<IOException>(() => WindowsCpuPmDeviceDriverSource.ReadRegistryNames(_ => new(0, "Other", 4)));
        Assert.Throws<IOException>(() => WindowsCpuPmDeviceDriverSource.ReadRegistryNames(index => new(0, index.ToString(), (uint)index.ToString().Length)));
    }
    [Fact]
    public void ExplicitNativeSuccessRequiresMatchingCompleteIndependentRegistry()
    {
        var platform = new Inventory();
        var result = CpuPmActiveDriverScan.Read(new WindowsCpuPmDeviceDriverSource(platform));
        Assert.True(result.Complete); Assert.Equal(0, result.MatchingActiveDrivers);
        Assert.Equal(1, platform.Calls);
        Assert.Equal(56, Marshal.SizeOf<WindowsCpuPmDeviceDriverSource.NativeEntry>());
        Assert.Equal(16, Marshal.OffsetOf<WindowsCpuPmDeviceDriverSource.NativeEntry>("Type").ToInt32());
    }
    [Theory]
    [InlineData("more-data")]
    [InlineData("other-error")]
    [InlineData("resume")]
    [InlineData("remaining")]
    [InlineData("too-many")]
    [InlineData("pointer")]
    [InlineData("unterminated")]
    [InlineData("query-hidden")]
    [InlineData("registry-error")]
    [InlineData("duplicate")]
    [InlineData("adapter")]
    public void NativePartialErrorsMalformedBuffersAndRegistryVisibilityNeverEstablishAbsence(string fault)
    {
        var platform = new Inventory { Fault = fault };
        var result = CpuPmActiveDriverScan.Read(new WindowsCpuPmDeviceDriverSource(platform));
        Assert.False(result.Complete); Assert.Equal(-1, result.MatchingActiveDrivers);
        Assert.Equal(1, platform.Calls);
    }
    private sealed class Inventory : ICpuPmDriverInventoryPlatform
    {
        internal string Fault = "";
        internal int Calls;
        public CpuPmDriverEnumeration Enumerate(nint buffer, int bytes)
        {
            Calls++; Assert.Equal(WindowsCpuPmDeviceDriverSource.BufferBytes, bytes);
            const int offset = 112;
            nint name = Fault == "pointer" ? buffer - 2 : buffer + offset;
            if (Fault == "unterminated") { Marshal.Copy(Enumerable.Repeat((byte)65, 514).ToArray(), 0, buffer + offset, 514); }
            else
            {
                byte[] bytesValue = System.Text.Encoding.Unicode.GetBytes((Fault == "default-amd" ? "AMDRyzenMasterDriverRenamed" : "Other") + "\0");
                Marshal.Copy(bytesValue, 0, buffer + offset, bytesValue.Length);
            }
            var entry = new WindowsCpuPmDeviceDriverSource.NativeEntry { Name = name, DisplayName = name, Type = 1, State = 4 };
            Marshal.StructureToPtr(entry, buffer, false);
            if (Fault == "duplicate") { Marshal.StructureToPtr(entry, buffer + 56, false); }
            return Fault switch
            {
                "more-data" => new(false, 234, 64, 1, 1),
                "other-error" => new(false, 5, 0, 1, 0),
                "resume" => new(true, 0, 0, 1, 1),
                "remaining" => new(true, 0, 64, 1, 0),
                "too-many" => new(true, 0, 0, 4097, 0),
                "duplicate" => new(true, 0, 0, 2, 0),
                _ => new(true, 0, 0, 1, 0)
            };
        }
        public IReadOnlyList<CpuPmRegistryDriver> ReadRegistryDrivers() => Fault switch
        {
            "default-amd" => [new("AMDRyzenMasterDriverRenamed", 1,
                OpenBlade.Protocol.CpuServiceInventory.DefaultDriverPath("AMDRyzenMasterDriverRenamed", 1, true, "", @"C:\Windows"))
                { ImageProvenance = OpenBlade.Protocol.CpuServiceImageProvenance.DefaultDriverPath }],
            "registry-error" => throw new IOException(),
            "query-hidden" => [new("Other", 1, "other.sys"), new("Hidden", 1, "hidden.sys")],
            "adapter" => [new("Other", 4, "other.sys")],
            _ => [new("Other", 1, "other.sys")]
        };
    }
    private static CpuPmDeviceDriverRecord Other(string name = "Other") => new(name, 1, 4, @"\SystemRoot\System32\drivers\other.sys");
    [Fact]
    public void IndependentInventoryDistinguishesStoppedRegistrationFromActiveDriver()
    {
        var absent = CpuPmActiveDriverScan.Read(new Source([Other(), new(WindowsCpuPmDriverRegistration.Name, 1, 1, WindowsCpuPmDriverRegistration.Image)]));
        Assert.True(absent.Complete); Assert.Equal(0, absent.MatchingActiveDrivers);
        Assert.True(new CpuPmDriverAbsence(1060, true, absent.Complete, absent.MatchingActiveDrivers).IsAbsent);
        Assert.False(new CpuPmDriverAbsence(0, true, absent.Complete, absent.MatchingActiveDrivers).IsAbsent);
        Assert.False(new CpuPmDriverAbsence(1060, false, absent.Complete, absent.MatchingActiveDrivers).IsAbsent);
    }
    [Theory]
    [InlineData("AMDRyzenMasterDriverV31", @"\SystemRoot\drivers\alias.sys", 4)]
    [InlineData("amDRyzenMasterDriverV30", @"C:\drivers\other.sys", 2)]
    [InlineData("RenamedService", @"\??\C:\Program Files\Razer\RazerAMDOverClock\AMDRyzenMasterDriver.sys", 3)]
    [InlineData("RenamedService", @"%SystemRoot%\drivers\AMDRyzenMasterDriver.sys", 7)]
    public void FixedNameOtherVersionsAndImageAliasesPreventAbsence(string name, string image, int state)
    {
        var result = CpuPmActiveDriverScan.Read(new Source([Other(), new(name, 1, (uint)state, image)]));
        Assert.True(result.Complete); Assert.Equal(1, result.MatchingActiveDrivers);
        Assert.False(new CpuPmDriverAbsence(1060, true, result.Complete, result.MatchingActiveDrivers).IsAbsent);
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown-state")]
    [InlineData("unknown-type")]
    [InlineData("missing-image")]
    [InlineData("foreign-expansion")]
    [InlineData("wrong-amd-type")]
    [InlineData("bad-name")]
    [InlineData("oversized")]
    [InlineData("late-read-error")]
    [InlineData("dispose-error")]
    public void AmbiguityBoundsAndAnyEnumerationFailureAreNotZeroActiveEvidence(string fault)
    {
        IEnumerable<CpuPmDeviceDriverRecord> records = fault switch
        {
            "duplicate" => [Other(), Other("other")],
            "unknown-state" => [Other() with { State = 0 }],
            "unknown-type" => [Other() with { Type = 0x10 }],
            "missing-image" => [Other() with { Image = "" }],
            "foreign-expansion" => [Other() with { Image = "%DriverImage%" }],
            "wrong-amd-type" => [Other() with { Name = WindowsCpuPmDriverRegistration.Name, Type = 2 }],
            "bad-name" => [Other(@"x\y")],
            "oversized" => Enumerable.Range(0, CpuPmActiveDriverScan.MaximumDrivers + 1).Select(i => Other("driver" + i)),
            "late-read-error" => LateFailure(),
            "dispose-error" => DisposeFailure(),
            _ => [],
        };
        var result = CpuPmActiveDriverScan.Read(new Source(records));
        Assert.False(result.Complete); Assert.Equal(-1, result.MatchingActiveDrivers);
        Assert.False(new CpuPmDriverAbsence(1060, true, result.Complete, result.MatchingActiveDrivers).IsAbsent);
    }
    private static IEnumerable<CpuPmDeviceDriverRecord> LateFailure()
    { yield return Other(); throw new IOException("private"); }
    private static IEnumerable<CpuPmDeviceDriverRecord> DisposeFailure()
    { try { yield return Other(); } finally { throw new IOException("private"); } }
    private sealed class Source(IEnumerable<CpuPmDeviceDriverRecord> values) : ICpuPmDeviceDriverSource
    { public IEnumerable<CpuPmDeviceDriverRecord> ReadAll() => values; }
}
