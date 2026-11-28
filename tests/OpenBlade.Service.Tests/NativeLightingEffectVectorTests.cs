using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service's RZ09-0581 animated lighting effects are pinned against
// managed. A frames row builds one effect directly, as ProductionLightingPort's
// CreateEffect does, with a scripted random source, and renders it at each
// elapsed tick. An advance row runs AdvanceFrameDeadline.
public sealed class NativeLightingEffectVectorTests
{
    private const string Vectors = "lighting-effect-vectors.json";
    private const string Regenerate = "OPENBLADE_REGENERATE_NATIVE_LIGHTING_EFFECT_VECTORS";

    private const string Comment =
        "Recorded by NativeLightingEffectVectorTests; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_LIGHTING_EFFECT_VECTORS set to this directory. "
        + "Times are TimeSpan ticks. A constants row records the managed timing constants. "
        + "A frames row builds one RZ09-0581 effect (colors as RRGGBB hex, directions and "
        + "durations by name) with a scripted palette selector that returns random in "
        + "order and a Tidal pair seed in hex, renders it at each elapsed tick, and "
        + "records for each sample GetNextFrameDelay, ApplyRefreshRateToDelay at 30 Hz "
        + "and 60 Hz, the canvas frame as RGB hex, and the SHA-256 of the encoded "
        + "feature report (transaction 1) projected onto layout bytes 0 and 255; draws "
        + "is how many selections the row used. An advance row records "
        + "AdvanceFrameDeadline for the effect at the rate.";

    private static readonly byte[] Layouts = [0, 255];

    [Fact]
    public void NativeLightingEffectVectors_MatchManagedEffects()
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, Vectors)
            : Path.Combine(output, Vectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "rows"], root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = Comment;
        }

        Assert.Equal(Comment, (string)root["comment"]!);
        JsonArray rows = root["rows"]!.AsArray();
        Assert.NotEmpty(rows);
        var failures = new List<string>();
        foreach (JsonNode? entry in rows)
        {
            JsonObject row = entry!.AsObject();
            JsonNode observed = Replay(row);
            if (!string.IsNullOrEmpty(output))
            {
                row["expect"] = observed;
            }
            else if (!JsonNode.DeepEquals(row["expect"], observed))
            {
                failures.Add($"{row["name"]}: {observed.ToJsonString()}");
            }
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString())
                .Append(",\n\"rows\":[\n")
                .AppendJoin(",\n", rows.Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
    }

    private static JsonNode Replay(JsonObject row) => (string)row["kind"]! switch
    {
        "constants" => new JsonObject
        {
            ["animatedFrameInterval"] = Pid02E0LightingTiming.AnimatedFrameInterval.Ticks,
            ["sixtyHzInterval"] = ProductionLightingPort.RefreshInterval(LightingRefreshRate.SixtyHz).Ticks,
            ["idleKeepaliveInterval"] = Pid02E0LightingTiming.IdleKeepaliveInterval.Ticks,
            ["spectrumCycle"] = Pid02E0LightingTiming.SpectrumCycle.Ticks,
            ["breathingCycle"] = Pid02E0LightingTiming.BreathingCycle.Ticks,
            ["tidalCycle"] = Pid02E0TidalOracle.CycleDuration.Ticks,
            ["starlightDurations"] = new JsonArray(
                Enum.GetValues<StarlightLightingDuration>()
                    .Select(duration => (JsonNode)ProductionLightingPort.StarlightDuration(duration).Ticks)
                    .ToArray()),
        },
        "frames" => Frames(row),
        "advance" => ProductionLightingPort.AdvanceFrameDeadline(
            Build(row["effect"]!.AsObject(), row, () => { }),
            TimeSpan.FromTicks((long)row["deadline"]!),
            TimeSpan.FromTicks((long)row["observed"]!),
            Enum.Parse<LightingRefreshRate>((string)row["rate"]!))!.Value.Ticks,
        _ => throw new InvalidOperationException($"Unknown row kind {row["kind"]}."),
    };

    private static JsonObject Frames(JsonObject row)
    {
        int draws = 0;
        IPid02E0LightingEffect effect = Build(row["effect"]!.AsObject(), row, () => draws++);
        var samples = new JsonArray();
        foreach (JsonNode? tick in row["elapsed"]!.AsArray())
        {
            TimeSpan elapsed = TimeSpan.FromTicks((long)tick!);
            TimeSpan delay = effect.GetNextFrameDelay(elapsed)!.Value;
            Pid02E0MatrixFrame frame = effect.RenderFrame(elapsed);
            var reports = new JsonArray();
            foreach (byte layout in Layouts)
            {
                byte[] report = Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(
                    new Pid02E0MatrixReport(1, Pid02E0RegionalFrameProjection.Project(frame, layout)));
                reports.Add(Convert.ToHexString(SHA256.HashData(report)));
            }

            samples.Add(new JsonObject
            {
                ["delay"] = delay.Ticks,
                ["thirtyHz"] = ProductionLightingPort.ApplyRefreshRateToDelay(
                    effect, delay, LightingRefreshRate.ThirtyHz).Ticks,
                ["sixtyHz"] = ProductionLightingPort.ApplyRefreshRateToDelay(
                    effect, delay, LightingRefreshRate.SixtyHz).Ticks,
                ["canvas"] = Convert.ToHexString(frame.ToArray()
                    .SelectMany(color => new[] { color.Red, color.Green, color.Blue })
                    .ToArray()),
                ["reports"] = reports,
            });
        }

        return new JsonObject { ["samples"] = samples, ["draws"] = draws };
    }

    private static IPid02E0LightingEffect Build(JsonObject spec, JsonObject row, Action drew)
    {
        MatrixRgb Color(string name)
        {
            byte[] bytes = Convert.FromHexString((string)spec[name]!);
            return new MatrixRgb(bytes[0], bytes[1], bytes[2]);
        }

        Queue<int> random = new(row["random"]?.AsArray().Select(value => (int)value!) ?? []);
        int Select(int bound)
        {
            drew();
            int index = random.Dequeue();
            Assert.InRange(index, 0, bound - 1);
            return index;
        }

        return (string)spec["kind"]! switch
        {
            "Spectrum" => new Pid02E0SpectrumEffect(),
            "Breathing" => (string)spec["mode"]! switch
            {
                "Single" => Pid02E0BreathingEffect.Single(Color("primary")),
                "Dual" => Pid02E0BreathingEffect.Dual(Color("primary"), Color("secondary")),
                "Random" => Pid02E0BreathingEffect.Random(new Pid02E0RandomPaletteColorSource(Select)),
                _ => throw new InvalidOperationException("Unknown Breathing mode."),
            },
            "Wave" => new Pid02E0WaveEffect(Enum.Parse<LightingDirection>((string)spec["direction"]!)),
            "Wheel" => new Pid02E0WheelEffect(
                Enum.Parse<LightingRotationDirection>((string)spec["direction"]!)),
            "Tidal" => new Pid02E0TidalEffect(
                Color("primary"),
                Color("secondary"),
                (bool)spec["random"]!,
                Enum.Parse<LightingDirection>((string)spec["direction"]!),
                (bool)spec["random"]!
                    ? new Pid02E0RandomPaletteColorPairSource(Convert.FromHexString((string)row["seed"]!))
                    : null),
            "Starlight" => new Pid02E0StarlightEffect(
                Enum.Parse<StarlightColorMode>((string)spec["mode"]!),
                spec["primary"] is null ? default : Color("primary"),
                spec["secondary"] is null ? default : Color("secondary"),
                ProductionLightingPort.StarlightDuration(
                    Enum.Parse<StarlightLightingDuration>((string)spec["duration"]!))),
            "Fire" => new Pid02E0FireEffect(),
            _ => throw new InvalidOperationException($"Unknown effect {spec["kind"]}."),
        };
    }
}
