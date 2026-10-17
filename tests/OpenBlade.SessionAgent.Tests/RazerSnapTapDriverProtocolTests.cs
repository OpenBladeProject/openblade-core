using System.Buffers.Binary;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class RazerSnapTapDriverProtocolTests
{
    private static readonly byte[] AHookFixture = Convert.FromHexString(
        "000000000100000000001E000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000");

    private static readonly byte[] DHookFixture = Convert.FromHexString(
        "0000000001000000000020000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000");

    [Fact]
    public void HookRecordsMatchPinnedVendorBuilderBytes()
    {
        Assert.Equal(AHookFixture, RazerSnapTapDriverProtocol.EncodeHook(0x1E));
        Assert.Equal(DHookFixture, RazerSnapTapDriverProtocol.EncodeHook(0x20));
    }

    [Fact]
    public void ClearAndOutputRecordsMatchPinnedVendorBuilderBytes()
    {
        byte[] aDown = Convert.FromHexString(
            "000000000100000000001E000000000000000000000000000000000000000000");
        byte[] aUp = Convert.FromHexString(
            "000000000100000000001E000100000000000000000000000000000000000000");
        byte[] dDown = Convert.FromHexString(
            "0000000001000000000020000000000000000000000000000000000000000000");
        byte[] dUp = Convert.FromHexString(
            "0000000001000000000020000100000000000000000000000000000000000000");

        Assert.Equal(aDown, RazerSnapTapDriverProtocol.EncodeClearHook(0x1E));
        Assert.Equal(dDown, RazerSnapTapDriverProtocol.EncodeClearHook(0x20));
        Assert.Equal(aDown, RazerSnapTapDriverProtocol.EncodeOutput(new(0x1E, true)));
        Assert.Equal(aUp, RazerSnapTapDriverProtocol.EncodeOutput(new(0x1E, false)));
        Assert.Equal(dDown, RazerSnapTapDriverProtocol.EncodeOutput(new(0x20, true)));
        Assert.Equal(dUp, RazerSnapTapDriverProtocol.EncodeOutput(new(0x20, false)));
    }

    [Fact]
    public void HookIndexIsAFourByteLittleEndianValue()
    {
        Assert.Equal(Convert.FromHexString("78563412"),
            RazerSnapTapDriverProtocol.EncodeHookIndex(0x12345678));
    }

    [Fact]
    public void HookEnableIsThePinnedFourByteBoolean()
    {
        Assert.Equal(Convert.FromHexString("01000000"),
            RazerSnapTapDriverProtocol.EncodeHookEnabled(true));
        Assert.Equal(Convert.FromHexString("00000000"),
            RazerSnapTapDriverProtocol.EncodeHookEnabled(false));
    }

    [Fact]
    public void HookSnapshotPreservesEveryPublicByteAndReturnsDefensiveCopies()
    {
        byte[] source = (byte[])AHookFixture.Clone();
        Assert.True(RazerSnapTapDriverProtocol.TryDecodeHook(
            source, RazerSnapTapDriverProtocol.HookRecordLength, out RazerSnapTapHookSnapshot? hook));
        RazerSnapTapHookSnapshot snapshot = Assert.IsType<RazerSnapTapHookSnapshot>(hook);

        source[0] = 0xFF;
        byte[] firstCopy = snapshot.CopyRecord();
        firstCopy[1] = 0xFF;

        Assert.Equal(AHookFixture, snapshot.CopyRecord());
        Assert.True(RazerSnapTapDriverProtocol.IsAdHook(snapshot));
        Assert.True(RazerSnapTapDriverProtocol.IsOwnedAdHook(snapshot));
    }

    [Fact]
    public void AnyExternalKeyboardAdSelectorConflictsButIsNotOwned()
    {
        byte[] external = (byte[])AHookFixture.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(external.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(external.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(external.AsSpan(16), 0x12345678);
        Assert.True(RazerSnapTapDriverProtocol.TryDecodeHook(
            external, external.Length, out RazerSnapTapHookSnapshot? hook));

        Assert.True(RazerSnapTapDriverProtocol.IsAdHook(hook!));
        Assert.False(RazerSnapTapDriverProtocol.IsOwnedAdHook(hook!));
    }

    [Fact]
    public void HookDecodeRequiresExactCompletionLength()
    {
        Assert.False(RazerSnapTapDriverProtocol.TryDecodeHook(
            AHookFixture, RazerSnapTapDriverProtocol.HookRecordLength - 1, out _));
        Assert.False(RazerSnapTapDriverProtocol.TryDecodeHook(
            AHookFixture, RazerSnapTapDriverProtocol.HookRecordLength + 1, out _));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x2C)]
    public void EncodersRejectAnythingOutsideTheFixedAdPair(ushort scanCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerSnapTapDriverProtocol.EncodeHook(scanCode));
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerSnapTapDriverProtocol.EncodeClearHook(scanCode));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerSnapTapDriverProtocol.EncodeOutput(new(scanCode, true)));
    }

    [Theory]
    [InlineData(0x1E, 0, true)]
    [InlineData(0x1E, 1, false)]
    [InlineData(0x20, 0, true)]
    [InlineData(0x20, 1, false)]
    public void HookNotificationDecodesOnlyTheSupportedPhysicalEvent(
        ushort scanCode,
        ushort flags,
        bool isDown)
    {
        byte[] notification = Enumerable.Repeat((byte)0xA5,
            RazerSnapTapDriverProtocol.NotificationLength).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(notification.AsSpan(22), scanCode);
        BinaryPrimitives.WriteUInt16LittleEndian(notification.AsSpan(24), flags);

        Assert.Equal(RazerSnapTapInputDecodeResult.Decoded,
            RazerSnapTapDriverProtocol.DecodeInput(
                notification, notification.Length, out SnapTapKeyStroke stroke));
        Assert.Equal(new SnapTapKeyStroke(scanCode, isDown), stroke);
    }

    [Fact]
    public void NonHookNotificationIsIgnoredBeforeItsOpaqueBodyIsExamined()
    {
        byte[] notification = Enumerable.Repeat((byte)0xA5,
            RazerSnapTapDriverProtocol.NotificationLength).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(8), 1);

        Assert.Equal(RazerSnapTapInputDecodeResult.Ignored,
            RazerSnapTapDriverProtocol.DecodeInput(notification, notification.Length, out _));
    }

    [Fact]
    public void KnownHookRejectsTruncationWrongTypeUnexpectedKeyAndExtendedFlags()
    {
        byte[] notification = new byte[RazerSnapTapDriverProtocol.NotificationLength];
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(notification.AsSpan(22), 0x1E);

        Assert.Equal(RazerSnapTapInputDecodeResult.Malformed,
            RazerSnapTapDriverProtocol.DecodeInput(notification, notification.Length - 1, out _));

        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(16), 3);
        Assert.Equal(RazerSnapTapInputDecodeResult.Malformed,
            RazerSnapTapDriverProtocol.DecodeInput(notification, notification.Length, out _));

        BinaryPrimitives.WriteUInt32LittleEndian(notification.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(notification.AsSpan(22), 0x2C);
        Assert.Equal(RazerSnapTapInputDecodeResult.Malformed,
            RazerSnapTapDriverProtocol.DecodeInput(notification, notification.Length, out _));

        BinaryPrimitives.WriteUInt16LittleEndian(notification.AsSpan(22), 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(notification.AsSpan(24), 2);
        Assert.Equal(RazerSnapTapInputDecodeResult.Malformed,
            RazerSnapTapDriverProtocol.DecodeInput(notification, notification.Length, out _));
    }
}
