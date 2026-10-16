using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsThxAdmissionQueriesTests
{
    [Fact]
    public void HardwareIdsPreserveWholeEntriesAndOnlyUseReturnedBytes()
    {
        const string first = @"SOUNDWIRE\SDCA_FUNCTION_11&MAN_025D&FUNC_0721&TYPE_0B&VER_01&ADR_04&SUBSYS_30101A58";
        const string second = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A581234";
        byte[] value = Encoding.Unicode.GetBytes(first + '\0' + second + "\0\0");
        byte[] buffer = [.. value, 0xff, 0xff];
        Assert.Equal([first, second], WindowsThxAdmissionQueries.ParseHardwareIds(buffer, (uint)value.Length, 0x2012));
        Assert.Empty(WindowsThxAdmissionQueries.ParseHardwareIds(new byte[4], 4, 0x2012));
    }

    [Theory]
    [InlineData("codec\0", 0x2012)]
    [InlineData("codec\0\0hidden\0\0", 0x2012)]
    [InlineData("\0codec\0\0", 0x2012)]
    [InlineData("codec\0\0", 0x12)]
    public void HardwareIdsRejectMalformedListOrWrongPropertyType(string value, uint type)
    {
        byte[] buffer = Encoding.Unicode.GetBytes(value);
        Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ParseHardwareIds(buffer, (uint)buffer.Length, type));
    }

    [Fact]
    public void HardwareIdsRejectInvalidSizesAndUnicode()
    {
        byte[] buffer = Encoding.Unicode.GetBytes("codec\0\0");
        foreach (uint count in new uint[] { 0, 2, 3, (uint)buffer.Length + 2, 65538 })
        {
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ParseHardwareIds(buffer, count, 0x2012));
        }
        Assert.Throws<DecoderFallbackException>(() =>
            WindowsThxAdmissionQueries.ParseHardwareIds([0x00, 0xd8, 0, 0, 0, 0], 6, 0x2012));
    }

    [Fact]
    public void NativeIdentityStringsRequireExactBoundsAndTermination()
    {
        Assert.Equal("device", WindowsThxAdmissionQueries.ReadTerminatedString("device\0old".ToCharArray(), 7));
        Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadTerminatedString("device".ToCharArray(), 7));
        Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadTerminatedString("device!".ToCharArray(), 7));
        Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadTerminatedString("dev\0ce\0".ToCharArray(), 7));
        Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadTerminatedString(['\0'], 1));
    }

    [Theory]
    [InlineData(5)] // Access denied is not absence.
    [InlineData(1072)] // Marked for deletion is an uncertain controller state.
    [InlineData(122)] // Insufficient buffer must not silently truncate a name.
    public void ServiceErrorsFailClosed(int error)
    {
        Assert.False(WindowsThxAdmissionQueries.ServiceIsAbsentOrThrow(1060));
        Win32Exception exception = Assert.Throws<Win32Exception>(() => WindowsThxAdmissionQueries.ServiceIsAbsentOrThrow(error));
        Assert.Equal(error, exception.NativeErrorCode);
    }

    [Fact]
    public void ServicePathCannotEscapeItsConfigurationBuffer()
    {
        const int bytes = 512;
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            byte[] data = Enumerable.Repeat((byte)0x41, bytes).ToArray();
            Marshal.Copy(data, 0, buffer, bytes);
            IntPtr path = IntPtr.Add(buffer, 128);
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, path));
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, IntPtr.Add(buffer, -2)));
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, buffer));
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, IntPtr.Add(buffer, 129)));
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, IntPtr.Add(buffer, bytes)));
            const string expected = @"C:\Windows\System32\reviewed.exe";
            byte[] value = Encoding.Unicode.GetBytes(expected + '\0');
            Marshal.Copy(value, 0, path, value.Length);
            Assert.Equal(expected, WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, path));
            Marshal.WriteInt16(path, 0);
            Assert.Throws<InvalidDataException>(() => WindowsThxAdmissionQueries.ReadBufferString(buffer, bytes, path));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData("  \"C:\\Program Files\\THX\\reviewed.exe\" -service", "C:\\Program Files\\THX\\reviewed.exe")]
    [InlineData("C:\\THX\\reviewed.exe -service", "C:\\THX\\reviewed.exe")]
    [InlineData("C:\\THX\\reviewed.exe", "C:\\THX\\reviewed.exe")]
    [InlineData("\"C:\\THX\\reviewed.exe", "")]
    public void ServiceCommandLineRetainsExistingBinaryNormalization(string commandLine, string expected)
    {
        Assert.Equal(expected, WindowsThxAdmissionQueries.NormalizeServiceBinary(commandLine));
    }
}
