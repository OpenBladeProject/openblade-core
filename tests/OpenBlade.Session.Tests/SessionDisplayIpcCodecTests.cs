using System.Buffers.Binary;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class SessionDisplayIpcCodecTests
{
    public static TheoryData<SessionDisplayRequest> Requests => new()
    {
        new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, Guid.NewGuid()),
        new SetRefreshRateRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.PluggedIn,
            "dynamic-48-240"),
        new SetHdrRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.Battery,
            true),
        new SetColorProfileRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.UsbC,
            "sRGB_CTR.icm"),
        new SetBrightnessRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.PluggedIn,
            75),
        new ApplyDisplayProfileRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", false, "Native_CTR.icm")),
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Request_RoundTripsEveryTypedOperation(SessionDisplayRequest request)
    {
        using var stream = new MemoryStream();

        await SessionDisplayIpcCodec.WriteRequestAsync(stream, request);
        stream.Position = 0;
        SessionDisplayRequest decoded = await SessionDisplayIpcCodec.ReadRequestAsync(stream);

        Assert.Equal(request, decoded);
        Assert.Equal(request.GetType(), decoded.GetType());
    }

    [Fact]
    public async Task Response_RoundTripsPostOperationSnapshot()
    {
        var response = new SessionDisplayResponse(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            SessionDisplayOperationStatus.Succeeded,
            null,
            Snapshot("dynamic-48-240", hdr: false, "sRGB_CTR.icm"));
        using var stream = new MemoryStream();

        await SessionDisplayIpcCodec.WriteResponseAsync(stream, response);
        stream.Position = 0;
        SessionDisplayResponse decoded = await SessionDisplayIpcCodec.ReadResponseAsync(stream);

        Assert.Equal(response.Version, decoded.Version);
        Assert.Equal(response.RequestId, decoded.RequestId);
        Assert.Equal(response.Status, decoded.Status);
        Assert.Equal(response.Error, decoded.Error);
        Assert.Equal(response.State.IsAvailable, decoded.State.IsAvailable);
        Assert.Equal(response.State.DisplayName, decoded.State.DisplayName);
        Assert.Equal(response.State.Width, decoded.State.Width);
        Assert.Equal(response.State.Height, decoded.State.Height);
        Assert.True(response.State.RefreshOptions.SequenceEqual(decoded.State.RefreshOptions));
        Assert.True(response.State.ColorProfiles.SequenceEqual(decoded.State.ColorProfiles));
        Assert.Equal("dynamic-48-240", decoded.State.CurrentRefreshKey);
        Assert.Equal("sRGB_CTR.icm", decoded.State.ActiveColorProfileId);
    }

    [Fact]
    public async Task ReadRequest_RejectsUnknownMembers()
    {
        string json = $$"""
            {"operation":"query","version":{{SessionDisplayIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","unexpected":true}
            """;
        using MemoryStream stream = Frame(json);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionDisplayIpcCodec.ReadRequestAsync(stream));
    }

    [Fact]
    public async Task WriteRequest_RejectsUnconfirmedSourceAndControlCharacters()
    {
        using var stream = new MemoryStream();
        var unknown = new SetHdrRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.ExternalUnknown,
            true);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionDisplayIpcCodec.WriteRequestAsync(stream, unknown));

        var malformed = new SetRefreshRateRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            PowerSource.PluggedIn,
            "fixed-240\r\nother");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionDisplayIpcCodec.WriteRequestAsync(stream, malformed));
    }

    [Fact]
    public async Task Codec_RejectsWrongVersionAndOversizedFrameBeforeAllocation()
    {
        using var wrongVersion = new MemoryStream();
        var request = new QueryDisplayRequest(
            SessionDisplayIpc.ProtocolVersion + 1,
            Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionDisplayIpcCodec.WriteRequestAsync(wrongVersion, request));

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            SessionDisplayIpc.MaximumMessageBytes + 1);
        using var oversized = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionDisplayIpcCodec.ReadRequestAsync(oversized));
    }

    [Fact]
    public async Task Response_AlwaysRequiresAValidPostOperationSnapshot()
    {
        var invalid = new SessionDisplayResponse(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid(),
            SessionDisplayOperationStatus.Succeeded,
            null,
            Snapshot("fixed-60", hdr: false, "native") with
            {
                RefreshOptions = null!,
            });
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionDisplayIpcCodec.WriteResponseAsync(stream, invalid));
    }

    private static MemoryStream Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }

    internal static DisplaySettingsSnapshot Snapshot(
        string refreshKey,
        bool hdr,
        string colorKey) => new(
        IsAvailable: true,
        DisplayName: "Internal display",
        Width: 2560,
        Height: 1600,
        CurrentRefreshKey: refreshKey,
        RefreshOptions:
        [
            new DisplayRefreshOption("fixed-60", "60 Hz", 60, 60, 60, false),
            new DisplayRefreshOption("fixed-240", "240 Hz", 240, 240, 240, false),
            new DisplayRefreshOption(
                "dynamic-48-240",
                "Dynamic (48-240 Hz)",
                48,
                240,
                240,
                true),
        ],
        HdrSupported: true,
        HdrEnabled: hdr,
        ColorProfilesEnabled: !hdr,
        ActiveColorProfileId: colorKey,
        ColorProfiles:
        [
            new DisplayColorProfile("native", "Native", colorKey == "native"),
            new DisplayColorProfile(
                "sRGB_CTR.icm",
                "sRGB",
                colorKey == "sRGB_CTR.icm"),
        ],
        Message: null);
}
