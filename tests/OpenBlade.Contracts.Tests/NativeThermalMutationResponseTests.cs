using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed class NativeThermalMutationResponseTests
{
    [Fact]
    public async Task ActualNativeMutationResponsesUseManagedV79CodecAndOutcomes()
    {
        // The evidence runner extracts actual emitted Rust test replies into a
        // held, hashed private input. There is deliberately no product fallback.
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_THERMAL_RESPONSES")
            ?? throw new InvalidOperationException("The prelisted native response input is required.");
        Assert.True(Path.IsPathFullyQualified(path));
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var seen = new HashSet<ControlOperationStatus>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(element.GetRawText());
            byte[] framed = new byte[bytes.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(framed, bytes.Length);
            bytes.CopyTo(framed, 4);
            using var stream = new MemoryStream(framed, writable: false);
            var response = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
            Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
            Assert.NotEqual(Guid.Empty, response.RequestId);
            Assert.True(response.Success);
            Assert.Null(response.Error);
            if (response.Result is not { } result) { continue; }
            seen.Add(result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            if (result.Status == ControlOperationStatus.Applied)
            {
                Assert.NotNull(response.Status);
                Assert.False(response.Status.WritesBlocked);
                Assert.Null(response.Status.Capabilities);
                Assert.Contains("GPU", result.Message, StringComparison.Ordinal);
            }
        }
        Assert.Contains(ControlOperationStatus.Stored, seen);
        Assert.Contains(ControlOperationStatus.Applied, seen);
        Assert.Contains(ControlOperationStatus.Blocked, seen);
    }
}
