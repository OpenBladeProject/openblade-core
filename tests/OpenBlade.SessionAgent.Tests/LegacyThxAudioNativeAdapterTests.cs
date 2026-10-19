using System.Reflection;
using System.Runtime.InteropServices;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class LegacyThxAudioNativeAdapterTests
{
    [Fact]
    public void OutputReadbackPreservesExactEndpointAndRejectsTruncation()
    {
        const string endpoint = "{0.0.0.00000000}.{12345678-1234-1234-1234-123456789ABC}";
        ushort[] buffer = new ushort[80];
        endpoint.Select(c => (ushort)c).ToArray().CopyTo(buffer, 0);
        Assert.Equal(endpoint, LegacyThxAudioNativeAdapter.ParseOutputDevice(buffer));
        Assert.Throws<InvalidDataException>(() => LegacyThxAudioNativeAdapter.ParseOutputDevice(new ushort[80]));
        Assert.Throws<InvalidDataException>(() => LegacyThxAudioNativeAdapter.ParseOutputDevice(Enumerable.Repeat((ushort)'a', 80).ToArray()));
        buffer[1] = 0xd800;
        Assert.Throws<InvalidDataException>(() => LegacyThxAudioNativeAdapter.ParseOutputDevice(buffer));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void BooleanReadbackUsesInt32Boolean(int value, bool expected) =>
        Assert.Equal(expected, LegacyThxAudioNativeAdapter.ParseBoolean(value));

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UnknownBooleanDoesNotInventState(int value) =>
        Assert.Throws<InvalidDataException>(() => LegacyThxAudioNativeAdapter.ParseBoolean(value));

    [Fact]
    public void MutationFailureAndOversizedNotificationRemainErrors()
    {
        Assert.Throws<COMException>(() => LegacyThxAudioNativeAdapter.ValidateMutationResult(unchecked((int)0x80004005), 0));
        Assert.Throws<InvalidDataException>(() => LegacyThxAudioNativeAdapter.ValidateMutationResult(0, 4097));
        LegacyThxAudioNativeAdapter.ValidateMutationResult(0, 4096);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\VSSrv.exe", true)]
    [InlineData("\"C:\\Windows\\System32\\VSSrv.exe\"", true)]
    [InlineData("C:\\Windows\\System32\\VSSrv.exe -service", false)]
    [InlineData("C:\\Windows\\System32\\VSSrv.exe\\1", false)]
    [InlineData("VSSrv.exe", false)]
    [InlineData("%SystemRoot%\\System32\\VSSrv.exe", false)]
    [InlineData("C:\\Users\\Someone\\VSSrv.exe", false)]
    public void ActivationPathIsExactAndHasNoArguments(string value, bool expected) =>
        Assert.Equal(expected, LegacyThxAudioNativeAdapter.IsExactServerPath(value, "C:\\Windows\\System32\\VSSrv.exe"));

    [Fact]
    public void ComMetadataPreservesVerifiedVtableAndFixedArrayWidths()
    {
        MethodInfo[] settings = typeof(LegacyThxAudioNativeAdapter.ILegacySettings).GetMethods().OrderBy(m => m.MetadataToken).ToArray();
        MethodInfo[] thx = typeof(LegacyThxAudioNativeAdapter.ILegacyThxSettings).GetMethods().OrderBy(m => m.MetadataToken).ToArray();
        Assert.Equal(18, settings.Length);
        Assert.Equal(22, thx.Length);
        Assert.Equal("Init", settings[0].Name);
        Assert.Equal("GetOutputDevice", settings[10].Name);
        Assert.Equal("GetSpatialProcessingState", thx[1].Name);
        Assert.Equal("SetSpatialProcessingState", thx[2].Name);
        Assert.Equal("GetListeningMode", thx[10].Name);
        Assert.Equal(typeof(uint), settings[0].GetParameters()[0].ParameterType);
        Assert.Equal(typeof(int).MakeByRefType(), thx[1].GetParameters()[0].ParameterType);
        Assert.Equal(80, settings[10].GetParameters()[0].GetCustomAttribute<MarshalAsAttribute>()!.SizeConst);
        Assert.Equal(31, thx[7].GetParameters()[0].GetCustomAttribute<MarshalAsAttribute>()!.SizeConst);
        Assert.Equal(typeof(ulong).MakeByRefType(), thx[2].GetParameters()[2].ParameterType);
        Assert.Equal(4096, thx[2].GetParameters()[3].GetCustomAttribute<MarshalAsAttribute>()!.SizeConst);
        Assert.Equal(UnmanagedType.LPStr, thx[2].GetParameters()[0].GetCustomAttribute<MarshalAsAttribute>()!.Value);
        Assert.All(settings.Concat(thx), method => Assert.True((method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0));
        Assert.Equal(0x9004u, LegacyThxAudioNativeAdapter.ActivationContext);
    }
}
