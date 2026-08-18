using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090581CapabilityDiagnosticsProtocolTests
{
    [Fact]
    public void TryDecode_PreservesRawBytesAndUsesBoundedFanCount()
    {
        byte[] fanPayload = new byte[80];
        fanPayload[0] = 2;
        fanPayload[1] = 1;
        fanPayload[2] = 2;
        fanPayload[79] = 0xA5;

        Assert.True(Rz090581CapabilityDiagnosticsProtocol.TryDecode(
            [0x01],
            [0x00],
            fanPayload,
            out OpenBlade.Contracts.Rz090581CapabilityDiagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Assert.Equal(0x01, diagnostics.SkuHardwareConfigurationRaw);
        Assert.Equal(0x00, diagnostics.MiniLedPanelResolutionRaw);
        Assert.Equal([0x01, 0x02], diagnostics.ThermalFanIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    public void TryDecodeThermalFanIds_AcceptsRepresentableCount(int count)
    {
        byte[] payload = new byte[80];
        payload[0] = checked((byte)count);

        Assert.True(Rz090581CapabilityDiagnosticsProtocol.TryDecodeThermalFanIds(
            payload,
            out IReadOnlyList<byte> fanIds));
        Assert.Equal(count, fanIds.Count);
    }

    [Fact]
    public void TryDecodeThermalFanIds_RejectsCountBeyondPayload()
    {
        byte[] payload = new byte[80];
        payload[0] = 80;

        Assert.False(Rz090581CapabilityDiagnosticsProtocol.TryDecodeThermalFanIds(
            payload,
            out IReadOnlyList<byte> fanIds));
        Assert.Empty(fanIds);
    }

    [Fact]
    public void SanitizedFixture_RepeatsCapturedBytesAcrossFourKnownStates()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        JsonElement root = document.RootElement;

        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal("1532", root.GetProperty("device").GetProperty("vendorId").GetString());
        Assert.Equal("02E0", root.GetProperty("device").GetProperty("productId").GetString());
        Assert.True(root.GetProperty("harness").GetProperty("readOnly").GetBoolean());

        JsonElement[] runs = root.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(4, runs.Length);
        foreach (JsonElement run in runs)
        {
            JsonElement[] responses = run.GetProperty("responses").EnumerateArray().ToArray();
            AssertCapturedRawByte(responses, "0D/8F", "00", 0x01);
            AssertCapturedRawByte(responses, "0D/8E", "00", 0x00);

            JsonElement fans = Assert.Single(
                responses,
                response => response.GetProperty("operation").GetString() == "0D/80");
            Assert.Equal(new string('0', 160), fans.GetProperty("requestPayloadHex").GetString());
            Assert.Equal("02", fans.GetProperty("expectedStatusHex").GetString());
            byte[] payload = Convert.FromHexString(
                fans.GetProperty("responsePayloadHex").GetString()!);
            Assert.True(Rz090581CapabilityDiagnosticsProtocol.TryDecodeThermalFanIds(
                payload,
                out IReadOnlyList<byte> fanIds));
            Assert.Equal([0x01, 0x02], fanIds);
        }
    }

    private static void AssertCapturedRawByte(
        JsonElement[] responses,
        string operation,
        string requestPayload,
        byte expectedRaw)
    {
        JsonElement response = Assert.Single(
            responses,
            item => item.GetProperty("operation").GetString() == operation);
        Assert.Equal(requestPayload, response.GetProperty("requestPayloadHex").GetString());
        Assert.Equal("02", response.GetProperty("expectedStatusHex").GetString());
        Assert.True(Rz090581CapabilityDiagnosticsProtocol.TryDecodeRawByte(
            Convert.FromHexString(response.GetProperty("responsePayloadHex").GetString()!),
            out byte raw));
        Assert.Equal(expectedRaw, raw);
    }

    private static string FixturePath() => RepositoryFile(
        "captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-performance-fan-discriminator-negative.json");

    private static string RepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(relativeSegments)}.");
    }
}
