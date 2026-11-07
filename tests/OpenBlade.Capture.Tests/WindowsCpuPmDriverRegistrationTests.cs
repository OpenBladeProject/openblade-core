using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// Buffer/layout-only. No SCM, registry, mutex, driver or process API is invoked.
public sealed class WindowsCpuPmDriverRegistrationTests
{
    private const ulong Address = 0x10000000;
    [Fact]
    public void X64ConfigurationAndStatusLayoutsMatchWin32DwordAndPointerAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 64 : 36, Marshal.SizeOf<WindowsCpuPmDriverRegistration.QueryConfig>());
        if (IntPtr.Size == 8)
        {
            string[] names = ["Type", "Start", "Error", "BinaryPath", "Group", "Tag", "Dependencies", "Account", "Display"];
            int[] offsets = [0, 4, 8, 16, 24, 32, 40, 48, 56];
            for (int i = 0; i < names.Length; i++) { Assert.Equal(offsets[i], Marshal.OffsetOf<WindowsCpuPmDriverRegistration.QueryConfig>(names[i]).ToInt32()); }
        }
        Assert.Equal(28, Marshal.SizeOf<WindowsCpuPmDriverRegistration.ServiceStatus>());
        Assert.Equal(36, Marshal.SizeOf<WindowsCpuPmDriverRegistration.ServiceStatusProcess>());
        Assert.Equal(0x10035u, WindowsCpuPmDriverRegistration.CreatedAccess);
        Assert.Equal("AMDRyzenMasterDriverV31", WindowsCpuPmDriverRegistration.Name);
        Assert.Equal(@"\??\C:\Program Files\Razer\RazerAMDOverClock\AMDRyzenMasterDriver.sys", WindowsCpuPmDriverRegistration.Image);
    }

    [Fact]
    public void ExactConfigAllowsOnlyEmptyOptionalFieldsAndFixedProfile()
    {
        byte[] config = Valid(); Assert.True(WindowsCpuPmDriverRegistration.ConfigurationMatches(config, Address));
        foreach (int field in new[] { 24, 40, 48 }) { BinaryPrimitives.WriteUInt64LittleEndian(config.AsSpan(field), 0); }
        Assert.True(WindowsCpuPmDriverRegistration.ConfigurationMatches(config, Address));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(32)]
    public void ChangedScalarConfigIsDenied(int field)
    {
        byte[] config = Valid(); BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(field), 2);
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(config, Address));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(56)]
    public void EveryPointerMustRemainAlignedInsideReturnedAllocationBeyondStruct(int field)
    {
        foreach (ulong pointer in new[] { Address - 2, Address + 8, Address + 65, Address + 512, ulong.MaxValue })
        {
            byte[] config = Valid(); BinaryPrimitives.WriteUInt64LittleEndian(config.AsSpan(field), pointer);
            Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(config, Address));
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("display")]
    [InlineData("group")]
    [InlineData("account")]
    [InlineData("dependency")]
    [InlineData("single-dependency-terminator")]
    [InlineData("unterminated")]
    [InlineData("invalid-utf16")]
    [InlineData("null-required")]
    public void StringsAndMultiszCannotBroadenFixedProfile(string fault)
    {
        byte[] config = Valid(); int field = fault switch { "display" => 56, "group" => 24, "account" => 48, "dependency" or "single-dependency-terminator" => 40, _ => 16 };
        int offset = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(config.AsSpan(field)) - Address));
        if (fault == "single-dependency-terminator") { BinaryPrimitives.WriteUInt64LittleEndian(config.AsSpan(field), Address + 510); }
        else if (fault == "unterminated") { Array.Fill(config, (byte)0xFF, offset, config.Length - offset); }
        else if (fault == "invalid-utf16") { config[offset] = 0; config[offset + 1] = 0xD8; config[offset + 2] = 0; config[offset + 3] = 0; }
        else if (fault == "null-required") { BinaryPrimitives.WriteUInt64LittleEndian(config.AsSpan(field), 0); }
        else { config[offset] = (byte)'X'; }
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(config, Address));
    }

    [Fact]
    public void BufferLengthAndAddressOverflowAreRejectedWithoutPointerDereference()
    {
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches([], Address));
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(new byte[63], Address));
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(new byte[8193], Address));
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(Valid(), 0));
        Assert.False(WindowsCpuPmDriverRegistration.ConfigurationMatches(Valid(), ulong.MaxValue - 10));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("lifetime")]
    [InlineData("hash")]
    [InlineData("nonce")]
    public void OwnerTagRequiresCanonicalRunLifetimePayloadAndSeparateNonce(string fault)
    {
        Guid run = Guid.NewGuid(), lifetime = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new CpuPmDriverOwnerTag(fault == "run" ? Guid.Empty : run,
            fault == "lifetime" ? Guid.Empty : lifetime, new(fault == "hash" ? 'g' : 'A', 64), new(fault == "nonce" ? 'B' : 'b', 64)));
    }

    [Fact]
    public void AlreadyCanceledMutexRequestStopsBeforeCreatingAnyNativeObject()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WindowsCpuPmDriverMutex.Acquire(cancel.Token));
    }

    private static byte[] Valid()
    {
        byte[] bytes = new byte[512]; int offset = 64;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 3); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        void Put(int field, string text)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(field), Address + checked((ulong)offset));
            byte[] encoded = Encoding.Unicode.GetBytes(text + '\0'); encoded.CopyTo(bytes, offset); offset += encoded.Length;
        }
        Put(16, WindowsCpuPmDriverRegistration.Image); Put(24, ""); Put(40, "\0"); Put(48, ""); Put(56, WindowsCpuPmDriverRegistration.Name);
        return bytes;
    }
}
