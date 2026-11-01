using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ProfileRequestValidationTests
{
    [Theory]
    [InlineData("pluggedIn")]
    [InlineData("battery")]
    [InlineData("usbC")]
    public async Task ReadRequest_RejectsNullPowerProfileOnBothProtocolReaders(string member)
    {
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(encoded,
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                ProfileDefaults.CreateSafeDefaults()), CancellationToken.None);
        JsonObject request = JsonNode.Parse(encoded.ToArray().AsSpan(sizeof(int)))!.AsObject();
        request["profiles"]![member] = null;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));

        using var input = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(input, CancellationToken.None));
        input.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(input, CancellationToken.None));
    }
}
