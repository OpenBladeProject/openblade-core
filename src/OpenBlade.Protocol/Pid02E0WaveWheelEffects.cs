using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class Pid02E0WaveEffect : IPid02E0LightingEffect
{
    private readonly LightingDirection direction;

    internal Pid02E0WaveEffect(LightingDirection direction)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        this.direction = direction;
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0WaveWheelOracle.FrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0WaveWheelOracle.Render(
            "Wave",
            direction.ToString(),
            Pid02E0WaveWheelOracle.FrameIndex(elapsed));
    }
}

internal sealed class Pid02E0WheelEffect : IPid02E0LightingEffect
{
    private readonly LightingRotationDirection direction;

    internal Pid02E0WheelEffect(LightingRotationDirection direction)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        this.direction = direction;
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0WaveWheelOracle.FrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0WaveWheelOracle.Render(
            "Wheel",
            direction.ToString(),
            Pid02E0WaveWheelOracle.FrameIndex(elapsed));
    }
}

/// <summary>
/// Sanitized automatic-color templates from stable, full one-second Synapse
/// cycles. The fixture contains no selected RGB values or USB identifiers.
/// </summary>
internal static class Pid02E0WaveWheelOracle
{
    internal const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_3_01_WaveWheelOracle.json";
    internal const string ApprovedFixtureSha256 =
        "CFC9B9365CF7C55069AD2782AE510F0838865EAB0681E7A45BAD1D9B932B432E";
    internal const int FrameCount = 25;
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(40);

    private static readonly Lazy<IReadOnlyDictionary<string, MatrixRgb[]>> Templates =
        new(ReadTemplates);

    internal static int FrameIndex(TimeSpan elapsed) => (int)(
        (elapsed.Ticks % CycleDuration.Ticks) / FrameInterval.Ticks);

    internal static Pid02E0MatrixFrame Render(
        string effect,
        string direction,
        int frameIndex)
    {
        if ((uint)frameIndex >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        if (!Templates.Value.TryGetValue(Key(effect, direction), out MatrixRgb[]? template))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        return new Pid02E0MatrixFrame(
            template.AsSpan(
                frameIndex * Pid02E0MatrixFrame.SlotCount,
                Pid02E0MatrixFrame.SlotCount));
    }

    private static IReadOnlyDictionary<string, MatrixRgb[]> ReadTemplates()
    {
        Assembly assembly = typeof(Pid02E0WaveWheelOracle).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Wave/Wheel oracle fixture is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, ApprovedFixtureSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Wave/Wheel oracle fixture hash is not approved.");
        }

        WaveWheelOracleDocument? document = JsonSerializer.Deserialize<WaveWheelOracleDocument>(
            bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling =
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            });
        if (document is null
            || document.SchemaVersion != 1
            || document.EvidenceRole != "SanitizedWaveWheelMatrixOracle"
            || document.SourceCaptureSha256 !=
                "F9E458CE3B37BCC882F6D85B97F56D22D55292F71313FFA23AF0E0A46287FBE0"
            || document.Target != new WaveWheelOracleTarget(
                "RZ09-0581", "1532", "02E0", "3.01")
            || document.CycleMilliseconds != 1000
            || document.FrameIntervalMilliseconds != 40
            || document.FrameCount != FrameCount
            || document.SlotCount != Pid02E0MatrixFrame.SlotCount
            || document.Templates.Length != 4)
        {
            throw new InvalidOperationException("The Wave/Wheel oracle fixture metadata is invalid.");
        }

        string[] expectedKeys =
        [
            Key("Wave", nameof(LightingDirection.Rightward)),
            Key("Wave", nameof(LightingDirection.Leftward)),
            Key("Wheel", nameof(LightingRotationDirection.Clockwise)),
            Key("Wheel", nameof(LightingRotationDirection.Counterclockwise)),
        ];
        var result = new Dictionary<string, MatrixRgb[]>(StringComparer.Ordinal);
        foreach (WaveWheelOracleTemplate template in document.Templates)
        {
            string key = Key(template.Effect, template.Direction);
            if (!expectedKeys.Contains(key, StringComparer.Ordinal)
                || !result.TryAdd(key, Decode(template)))
            {
                throw new InvalidOperationException("A Wave/Wheel oracle template is unexpected.");
            }
        }

        if (!expectedKeys.All(result.ContainsKey))
        {
            throw new InvalidOperationException("A Wave/Wheel oracle template is missing.");
        }

        return result;
    }

    private static MatrixRgb[] Decode(WaveWheelOracleTemplate template)
    {
        int expectedBytes = FrameCount * Pid02E0MatrixFrame.SlotCount * 3;
        if (template.Encoding != "deflate-base64-rgb24"
            || template.UncompressedBytes != expectedBytes)
        {
            throw new InvalidOperationException("A Wave/Wheel oracle template is invalid.");
        }

        byte[] compressed = Convert.FromBase64String(template.Data);
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        byte[] raw = new byte[expectedBytes];
        deflate.ReadExactly(raw);
        if (deflate.ReadByte() != -1)
        {
            throw new InvalidOperationException("A Wave/Wheel template has trailing data.");
        }

        MatrixRgb[] values = new MatrixRgb[FrameCount * Pid02E0MatrixFrame.SlotCount];
        for (int index = 0; index < values.Length; index++)
        {
            int offset = index * 3;
            values[index] = new MatrixRgb(raw[offset], raw[offset + 1], raw[offset + 2]);
            int slot = index % Pid02E0MatrixFrame.SlotCount;
            if (Pid02E0MatrixLayout.IsHole(slot) != (values[index] == MatrixRgb.Black))
            {
                throw new InvalidOperationException("A Wave/Wheel oracle slot mask is invalid.");
            }
        }

        return values;
    }

    private static string Key(string effect, string direction) => $"{effect}:{direction}";

    private sealed record WaveWheelOracleDocument(
        int SchemaVersion,
        string EvidenceRole,
        WaveWheelOracleTarget Target,
        string SourceCaptureSha256,
        int CycleMilliseconds,
        int FrameIntervalMilliseconds,
        int FrameCount,
        int SlotCount,
        WaveWheelOracleTemplate[] Templates);

    private sealed record WaveWheelOracleTarget(
        string ModelNumber,
        string VendorIdHex,
        string ProductIdHex,
        string Bios);

    private sealed record WaveWheelOracleTemplate(
        string Effect,
        string Direction,
        double StartSeconds,
        int FirstSourceFrame,
        int LastSourceFrame,
        string Encoding,
        int UncompressedBytes,
        string Data);
}
