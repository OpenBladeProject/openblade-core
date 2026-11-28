using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class NativeThermalEdgeFixtureTests
{
    [NativeThermalEdgeFact]
    public async Task WriteCurrentManagedWireAndPolicyFixtures()
    {
        string root = RequiredRoot("OPENBLADE_EDGE_MANAGED_ROOT");
        string oldWire = RequiredRoot("OPENBLADE_EDGE_V79_INPUT_ROOT");
        string generated = Path.Combine(root, "generated");
        Assert.False(Directory.Exists(generated));
        Directory.CreateDirectory(generated);
        foreach (string name in new[] { "get-status", "observe-state", "subscribe-status" })
        {
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(oldWire, name + "-v79.frames"));
            using var oldRequest = new MemoryStream(original, writable: false);
            await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(() => IpcMessageCodec.ReadRequestAsync(oldRequest, CancellationToken.None));
            using var output = new MemoryStream();
            int offset = 0;
            int frame = 0;
            while (offset < original.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(offset, 4));
                JsonNode node = JsonNode.Parse(original.AsSpan(offset + 4, length))!;
                Assert.Equal(79, node["version"]!.GetValue<int>());
                node["version"] = OpenBladeIpc.ProtocolVersion;
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(node);
                using var current = Framed(json);
                if (frame++ == 0)
                {
                    ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(current, CancellationToken.None);
                    await IpcMessageCodec.WriteRequestAsync(output, request, CancellationToken.None);
                }
                else
                {
                    ServiceServerFrame response = await IpcMessageCodec.ReadServerFrameAsync(current, CancellationToken.None);
                    await IpcMessageCodec.WriteServerFrameAsync(output, response, CancellationToken.None);
                }
                offset += length + 4;
            }
            Assert.Equal(original.Length, offset);
            await File.WriteAllBytesAsync(Path.Combine(generated, $"{name}-v{OpenBladeIpc.ProtocolVersion}.frames"), output.ToArray());
        }
        foreach (bool maximum in new[] { false, true })
        {
            string leaf = maximum ? "custom-gpu-maximum" : "custom-basic";
            var expected = new PersistentOverrideState(PerformancePreset.Custom, null, null,
                new CustomPerformanceSettings(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High) { MaximumFans = maximum },
                GpuClockTuning: maximum ? new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500)) : null);
            var store = new PersistentOverrideStore(Path.Combine(generated, leaf + ".json"));
            await store.SaveAsync(expected, CancellationToken.None);
            PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);
            Assert.False(loaded.Recovered);
            Assert.Equal(expected, loaded.Overrides);
        }
        foreach ((FanMode baseline, FanMode application) in new[] { (FanMode.Automatic, FanMode.Fixed), (FanMode.Fixed, FanMode.Automatic), (FanMode.Automatic, FanMode.ManualCurve) })
        {
            FanControlSettings Fan(FanMode mode) => new(mode, 3700, ManualFanCurve.Default);
            ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
            var profiles = defaults with
            {
                PluggedIn = defaults.PluggedIn with { Performance = PerformancePreset.Balanced, Fans = baseline, FanControl = Fan(baseline) },
                ApplicationProfiles = [new ApplicationProfileDefinition(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Game", @"C:\Games\Game.exe", ApplicationActivationMode.Foreground,
                    new ApplicationProfileOverrideSet { PluggedIn = new ApplicationPowerProfileOverride { FanControl = Fan(application) } })],
            };
            ProfileSetValidator.CreateUnverifiedSafeDefaults().Validate(profiles);
            Assert.Null(profiles.ApplicationProfiles[0].Overrides.PluggedIn!.Fans);
            string path = Path.Combine(generated, $"app-fans-{(int)baseline}-{(int)application}.json");
            var store = new ProfileStore(path, ProfileSetValidator.CreateUnverifiedSafeDefaults());
            await store.SaveAsync(profiles, CancellationToken.None);
            byte[] original = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(Path.Combine(root, Path.GetFileName(path) + ".before-reload"), original);
            ProfileLoadResult loaded = await store.LoadAsync(CancellationToken.None);
            Assert.False(loaded.RecoveredFromInvalidFile);
            Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
            Assert.Equal(ConfigurationQuarantineReason.None, loaded.QuarantineReason);
            Assert.Equal(profiles.PluggedIn, loaded.Profiles.PluggedIn);
            ApplicationPowerProfileOverride app = Assert.Single(loaded.Profiles.ApplicationProfiles).Overrides.PluggedIn!;
            Assert.Null(app.Fans);
            Assert.Equal(application, app.FanControl!.Mode);
            Assert.Equal(3700, app.FanControl.FixedSpeedRpm);
            Assert.Equal(ManualFanCurve.Default, app.FanControl.ManualCurve);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        var invalid = new PersistentOverrideState(null, null, null, FanControl: FanControlSettings.FromLegacy(FanMode.Fixed));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PersistentOverrideStore(Path.Combine(generated, "invalid-fan.json")).SaveAsync(invalid, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(generated, "invalid-fan.json")));
    }

    private static string RequiredRoot(string name)
    {
        string value = Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Explicit fixture input {name} is required.");
        Assert.True(Path.IsPathFullyQualified(value));
        Assert.True(Directory.Exists(value));
        return Path.GetFullPath(value);
    }

    private static MemoryStream Framed(byte[] json)
    {
        byte[] bytes = new byte[json.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, json.Length);
        json.CopyTo(bytes, 4);
        return new MemoryStream(bytes, writable: false);
    }
}

internal sealed class NativeThermalEdgeFactAttribute : FactAttribute
{
    public NativeThermalEdgeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_EDGE_FIXTURES") != "1")
        {
            Skip = "Requires explicit OPENBLADE_NATIVE_EDGE_FIXTURES=1 and private fixture inputs.";
        }
    }
}
