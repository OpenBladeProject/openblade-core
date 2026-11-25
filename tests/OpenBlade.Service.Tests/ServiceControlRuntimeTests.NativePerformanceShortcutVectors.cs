using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service selects the next performance shortcut preset with a port of
// PerformanceShortcutSelection. These rows pin the managed choice for every
// current preset on each cycle and preset set the RZ09-0581 Service can reach;
// the Rust tests replay them through openblade_domain::performance_shortcut.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativePerformanceShortcutVectors = "performance-shortcut-vectors.json";
    private const string RegenerateNativePerformanceShortcutVectors =
        "OPENBLADE_REGENERATE_NATIVE_PERFORMANCE_SHORTCUT_VECTORS";

    private const string NativePerformanceShortcutComment =
        "Recorded by ServiceControlRuntimeTests.NativePerformanceShortcutVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_PERFORMANCE_SHORTCUT_VECTORS set to this directory. Each row runs "
        + "PerformanceShortcutSelection.TrySelectNext (cycle pluggedIn, usbC or battery) or TrySelectNextOrdinary "
        + "(cycle ordinary) over the admitted presets (null is an unverified capability) for every defined current "
        + "preset and records the selected preset, or null when nothing is selected. The rz09-0581 set is the "
        + "production RZ09-0581 battery capability's PerformancePresets.";

    [Fact]
    public void NativePerformanceShortcutVectors_MatchManagedSelection()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativePerformanceShortcutVectors);
        IReadOnlySet<PerformancePreset> rz09_0581 =
            ProductionBatteryFactory.CreateWindows().Capabilities.PerformancePresets;
        var sets = new (string Name, IReadOnlySet<PerformancePreset>? Presets)[]
        {
            ("rz09-0581", rz09_0581),
            // The ordinary cycle without Custom, when no Custom intent is admitted.
            ("rz09-0581-without-custom", rz09_0581.Where(preset => preset != PerformancePreset.Custom).ToHashSet()),
            ("unverified", null),
        };
        var rows = new JsonArray();
        foreach (string cycle in new[] { "pluggedIn", "usbC", "battery", "ordinary" })
        {
            foreach ((string name, IReadOnlySet<PerformancePreset>? presets) in sets)
            {
                var next = new JsonArray();
                foreach (PerformancePreset current in Enum.GetValues<PerformancePreset>())
                {
                    PerformancePreset value = default;
                    bool selected = cycle switch
                    {
                        "pluggedIn" => PerformanceShortcutSelection.TrySelectNext(PowerSource.PluggedIn, presets, current, out value),
                        "usbC" => PerformanceShortcutSelection.TrySelectNext(PowerSource.UsbC, presets, current, out value),
                        "battery" => PerformanceShortcutSelection.TrySelectNext(PowerSource.Battery, presets, current, out value),
                        _ => PerformanceShortcutSelection.TrySelectNextOrdinary(presets, current, out value),
                    };
                    next.Add(selected ? JsonValue.Create((int)value) : null);
                }

                rows.Add(new JsonObject
                {
                    ["cycle"] = cycle,
                    ["admitted"] = name,
                    ["presets"] = presets is null
                        ? null
                        : new JsonArray([.. presets.Order().Select(preset => (JsonNode)(int)preset)]),
                    ["next"] = next,
                });
            }
        }

        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativePerformanceShortcutVectors)
            : Path.Combine(output, NativePerformanceShortcutVectors);
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativePerformanceShortcutComment).ToJsonString())
                .Append(",\n\"selection\":[\n")
                .AppendJoin(",\n", rows.Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "selection"], recorded.Select(member => member.Key).ToArray());
        Assert.Equal(NativePerformanceShortcutComment, recorded["comment"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recorded["selection"], rows), rows.ToJsonString());
    }
}
