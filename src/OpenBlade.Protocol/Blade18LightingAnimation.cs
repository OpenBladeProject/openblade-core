using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Fire/Wheel use exact-model observed colors. Starlight shares a software
/// twinkle pattern over this model's 96 RGB addresses, without a borrowed key
/// mask. These are experimental renderers without firmware RGB readback.
/// </summary>
internal static class Blade18LightingAnimation
{
    internal const string ResourceName = "OpenBlade.Protocol.Fixtures.Blade18FireWheelFrames.json";
    internal const string ApprovedSha256 = "4AB4D50E9B7868D8F1E611000ABEF286532924A88B7BCDDCA69EB991582090C4";
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Lazy<(Template Wheel, Template Fire)> Templates = new(ReadTemplates);

    internal static Blade18LightingFrame Render(LightingSettings settings, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        if (!LightingSettingsValidation.IsWellFormed(settings)
            || settings.Effect is not (LightingEffect.Fire or LightingEffect.Wheel or LightingEffect.Starlight))
        {
            throw new ArgumentException("Only typed Fire, Wheel and Starlight animations are supported.", nameof(settings));
        }
        if (settings.Parameters is StarlightLightingParameters starlight)
        {
            return RenderStarlight(starlight, elapsed);
        }
        Template template = settings.Effect == LightingEffect.Fire
            ? Templates.Value.Fire : Templates.Value.Wheel;
        long phase = elapsed.Ticks % template.CycleTicks;
        if (settings.Parameters is WheelLightingParameters
            { Direction: LightingRotationDirection.Counterclockwise })
        {
            phase = (template.CycleTicks - phase) % template.CycleTicks;
        }
        int index = template.Frames.Length - 1;
        while (index > 0 && template.Frames[index].PhaseTicks > phase)
        {
            index--;
        }
        return new Blade18LightingFrame(template.Frames[index].Colors);
    }

    private static Blade18LightingFrame RenderStarlight(
        StarlightLightingParameters parameters, TimeSpan elapsed)
    {
        static MatrixRgb Color(RgbColor? value) => value is { } color
            ? new(color.Red, color.Green, color.Blue) : default;
        var pattern = new StarlightPattern(parameters.ColorMode,
            Color(parameters.PrimaryColor), Color(parameters.SecondaryColor),
            ProductionLightingPort.StarlightDuration(parameters.Duration));
        var colors = new RgbColor[Blade18LightingFrame.SlotCount];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            MatrixRgb color = pattern.RenderSlot(slot, elapsed);
            colors[slot] = new(color.Red, color.Green, color.Blue);
        }
        return new(colors);
    }

    private static (Template Wheel, Template Fire) ReadTemplates()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Blade 18 animation fixture is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != ApprovedSha256)
        {
            throw new InvalidOperationException("The Blade 18 animation fixture is not reviewed.");
        }
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        JsonElement source = root.GetProperty("source");
        JsonElement transport = root.GetProperty("transport");
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || source.GetProperty("productIdHex").GetString() != "02A0"
            || source.GetProperty("vendorIdHex").GetString() != "1532"
            || source.GetProperty("bundleSha256").GetString()
                != "1485C6A5DFE829D2BFA0EE53BBAB60B49C19150F62487456C04DEF1F0DAB99CD"
            || transport.GetProperty("rows").GetInt32() != Blade18LightingFrame.Rows
            || transport.GetProperty("columns").GetInt32() != Blade18LightingFrame.Columns)
        {
            throw new InvalidOperationException("The Blade 18 animation evidence target is invalid.");
        }
        JsonElement templates = root.GetProperty("templates");
        return (ReadTemplate(templates.GetProperty("wheelClockwise"), "Wheel", 21),
            ReadTemplate(templates.GetProperty("fire"), "Fire", 32));
    }

    private static Template ReadTemplate(JsonElement template, string effect, int count)
    {
        double milliseconds = template.GetProperty("cycleDurationMilliseconds").GetDouble();
        JsonElement frames = template.GetProperty("frames");
        if (template.GetProperty("effect").GetString() != effect
            || template.GetProperty("frameCount").GetInt32() != count
            || frames.GetArrayLength() != count
            || !double.IsFinite(milliseconds) || milliseconds is <= 0 or > 2000)
        {
            throw new InvalidOperationException("The Blade 18 animation template is invalid.");
        }
        long cycle = TimeSpan.FromMilliseconds(milliseconds).Ticks;
        var result = new Frame[count];
        int index = 0;
        long previous = -1;
        foreach (JsonElement frame in frames.EnumerateArray())
        {
            long phase = TimeSpan.FromMilliseconds(frame.GetProperty("phaseMilliseconds").GetDouble()).Ticks;
            JsonElement rows = frame.GetProperty("rgbRowsHex");
            if (phase <= previous || phase >= cycle || index == 0 && phase != 0
                || rows.GetArrayLength() != Blade18LightingFrame.Rows)
            {
                throw new InvalidOperationException("The Blade 18 frame timing or geometry is invalid.");
            }
            var colors = new RgbColor[Blade18LightingFrame.SlotCount];
            int row = 0;
            foreach (JsonElement rgbRow in rows.EnumerateArray())
            {
                byte[] rgb = Convert.FromHexString(rgbRow.GetString()!);
                if (rgb.Length != Blade18LightingFrame.Columns * 3)
                {
                    throw new InvalidOperationException("The Blade 18 frame row is invalid.");
                }
                for (int column = 0; column < Blade18LightingFrame.Columns; column++)
                {
                    int offset = column * 3;
                    colors[row * Blade18LightingFrame.Columns + column] =
                        new(rgb[offset], rgb[offset + 1], rgb[offset + 2]);
                }
                row++;
            }
            result[index++] = new(phase, colors);
            previous = phase;
        }
        return new(cycle, result);
    }

    private sealed record Template(long CycleTicks, Frame[] Frames);
    private sealed record Frame(long PhaseTicks, RgbColor[] Colors);
}
