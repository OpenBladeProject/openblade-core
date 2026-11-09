using System.Runtime.InteropServices;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Testing;

namespace OpenBlade.Core.Native.Tests;

public sealed class NativeAutomationPolicyTests
{
    private static NativeAutomationPolicy Load()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_LIBRARY")
            ?? throw new InvalidOperationException("Actual native artifact is required.");
        NativeAutomationLoadResult loaded = NativeAutomationPolicy.Load(path);
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        return loaded.Policy!;
    }

    [Fact]
    public void CanonicalTokensMatchFixturesAndIndependentFrozenManagedDecisions()
    {
        using NativeAutomationPolicy policy = Load();
        foreach (AutomationCase row in AutomationParityFixture.Read())
        {
            NativeAutomationResult actual = row.Operation == 0
                ? policy.SelectPerformance(row.Temporary, row.Persistent, row.Application)
                : policy.SelectLighting(row.Profile, row.Confirmed, row.Temporary,
                    row.Persistent, row.ExternalUnknown, row.Application);
            Assert.True(actual.IsSuccess, actual.Error?.ToString());
            Assert.Equal(row.Origin, (uint)actual.Selection!.Value.Origin);
            Assert.Equal(row.Provenance, (uint)actual.Selection.Value.Source);
            VerifyFrozenBaseline(policy, row);
        }
    }

    [Fact]
    public void DecodedPerformanceSourceMatchesSharedMatrixAndFrozenPredicate()
    {
        using NativeAutomationPolicy policy = Load();
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Parity", "performance-source.json")));
        JsonElement root = fixture.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("8a6e49d7bcffc52c84e8d447711f9192385e9da6", root.GetProperty("sourceRevision").GetString());
        PowerSource[] sources = root.GetProperty("sources").EnumerateArray()
            .Select(item => (PowerSource)item.GetInt32()).ToArray();
        Assert.Equal(Enum.GetValues<PowerSource>().Concat(
            [(PowerSource)(-1), (PowerSource)int.MinValue, (PowerSource)int.MaxValue]), sources);

        var presets = new List<PerformancePreset>();
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            Assert.Equal(2, row.GetArrayLength());
            var preset = (PerformancePreset)row[0].GetInt32();
            presets.Add(preset);
            Assert.Equal(sources.Length, row[1].GetArrayLength());
            for (int index = 0; index < sources.Length; index++)
            {
                PowerSource source = sources[index];
                bool expected = row[1][index].GetBoolean();
                Assert.Equal(expected, ManagedAutomationPolicyBaseline.IsPerformanceCompatibleWithSource(preset, source));
                var (eligible, error) = policy.CheckPerformanceSource(preset, source);
                Assert.Null(error);
                Assert.Equal(expected, eligible);
            }
        }

        Assert.Equal(Enum.GetValues<PerformancePreset>().Concat(
            [(PerformancePreset)(-1), (PerformancePreset)10, (PerformancePreset)int.MinValue, (PerformancePreset)int.MaxValue]), presets);
    }

    [Fact]
    public void PerformanceSourceRejectsInconsistentNativeResult()
    {
        NativeErrorRecord error = NativeErrorRecord.Create();
        Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult,
            NativeAutomationPolicy.ReadPerformanceSourceEligibility(2, 0, error, 0).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult,
            NativeAutomationPolicy.ReadPerformanceSourceEligibility(1, 0, error, 1).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult,
            NativeAutomationPolicy.ReadPerformanceSourceEligibility(1, 5, error, 0).Error!.Kind);
    }

    [Fact]
    public void AllPresenceCombinationsPreserveOriginalValuesAndNullOverrideContents()
    {
        using NativeAutomationPolicy policy = Load();
        for (int flags = 0; flags < 64; flags++)
        {
            var row = new AutomationCase
            {
                Id = "exhaustive",
                Operation = 1,
                Profile = (flags & 1) != 0,
                Confirmed = (flags & 2) != 0,
                Temporary = (flags & 4) != 0,
                Persistent = (flags & 8) != 0,
                ExternalUnknown = (flags & 16) != 0,
                Application = (flags & 32) != 0,
                Origin = 0,
                Provenance = 0,
            };
            VerifyFrozenBaseline(policy, row);
            VerifyFrozenBaseline(policy, row, nullOverrideContents: true);
            VerifyFrozenBaseline(policy, row with { Operation = 0 });
        }
    }

    private static void VerifyFrozenBaseline(NativeAutomationPolicy policy, AutomationCase row,
        bool nullOverrideContents = false)
    {
        SettingValueSource profileSource = row.Application
            ? SettingValueSource.ApplicationProfile : SettingValueSource.PowerProfile;
        if (row.Operation == 0)
        {
            // Zero and unsupported scalar values are data, not ABI enums.
            var temporary = row.Temporary ? new PerformanceOverride((PerformancePreset)999,
                OverrideLifetime.UntilPowerSourceChange) : null;
            var persistent = row.Persistent ? new PerformanceOverride((PerformancePreset)(-1),
                OverrideLifetime.Persistent) : null;
            ResolvedSetting<PerformancePreset> expected = ManagedAutomationPolicyBaseline.ResolvePerformance(
                (PerformancePreset)0, temporary, persistent, profileSource);
            NativeAutomationSelection token = policy.SelectPerformance(
                temporary is not null, persistent is not null, row.Application).Selection!.Value;
            PerformancePreset value = token.Origin switch
            {
                AutomationValueOrigin.Profile => (PerformancePreset)0,
                AutomationValueOrigin.Temporary => temporary!.Value,
                AutomationValueOrigin.Persistent => persistent!.Value,
                _ => throw new InvalidOperationException("Invalid performance token."),
            };
            Assert.Equal(expected, new(value, token.Source));
            return;
        }

        LightingSettings basis = LightingSettingsFactory.Static(new(17, 0, 255), 0);
        // Equal records with distinct identities must remain the selected original object.
        LightingSettings? profileLighting = row.Profile ? basis with { } : null;
        LightingSettings? observed = row.Confirmed ? basis with { } : null;
        var temporaryLighting = row.Temporary ? new ManualOverride<LightingSettings>(
            nullOverrideContents ? null! : basis with { }, OverrideLifetime.UntilPowerSourceChange) : null;
        var persistentLighting = row.Persistent ? new ManualOverride<LightingSettings>(
            nullOverrideContents ? null! : basis with { }, OverrideLifetime.Persistent) : null;
        PowerProfile profile = ProfileDefaults.CreateSafeDefaults().PluggedIn with { Lighting = profileLighting! };
        var confirmed = new ConfirmedDeviceState(null, null, null, observed);
        ResolvedSetting<LightingSettings> baseline = ManagedAutomationPolicyBaseline.ResolveLighting(
            profile, confirmed, row.Application,
            row.ExternalUnknown ? PowerSource.ExternalUnknown : PowerSource.PluggedIn,
            temporaryLighting, persistentLighting);
        NativeAutomationSelection selection = policy.SelectLighting(profileLighting is not null,
            observed is not null, temporaryLighting is not null, persistentLighting is not null,
            row.ExternalUnknown, row.Application).Selection!.Value;
        LightingSettings? selected = selection.Origin switch
        {
            AutomationValueOrigin.Profile => profileLighting,
            AutomationValueOrigin.Confirmed => observed ?? profileLighting,
            AutomationValueOrigin.Temporary => temporaryLighting!.Value,
            AutomationValueOrigin.Persistent => persistentLighting!.Value,
            _ => throw new InvalidOperationException("Invalid lighting token."),
        };
        Assert.Equal(baseline.Source, selection.Source);
        Assert.Same(baseline.Value, selected);
    }

    [Fact]
    public unsafe void LayoutsAndRawFailurePublicationAreBounded()
    {
        Assert.True(NativeAutomationPolicy.LayoutsMatch());
        Assert.Equal(16, Marshal.SizeOf<NativeAutomationRequest>());
        Assert.Equal(12, Marshal.SizeOf<NativeAutomationRecord>());
        using NativeAutomationPolicy policy = Load();
        NativeAutomationRequest request = new() { StructSize = 16, Operation = 1, HasProfile = 1 };
        NativeAutomationRecord result = new() { StructSize = 12, Origin = 99, Provenance = 98 };
        NativeErrorRecord error = NativeErrorRecord.Create();
        uint written = 999;
        uint status = policy.RawSelect(2, null, &result, &written, &error);
        Assert.Equal(1u, status);
        Assert.Equal(0u, written);
        Assert.Equal(99u, result.Origin);
        request.Reserved1 = 1;
        status = policy.RawSelect(NativeAutomationPolicy.AutomationAbiVersion,
            &request, &result, &written, &error);
        Assert.Equal(2u, status);
        Assert.Equal(3u, error.Detail);
        Assert.Equal(99u, result.Origin);
        request.Reserved1 = 0;
        request.HasTemporary = 2;
        Assert.Equal(2u, policy.RawSelect(NativeAutomationPolicy.AutomationAbiVersion,
            &request, &result, &written, &error));
        Assert.Equal(99u, result.Origin);
        request.HasTemporary = 0;
        Assert.Equal(0u, policy.RawSelect(NativeAutomationPolicy.AutomationAbiVersion,
            &request, &result, &written, &error));
        Assert.Equal(0u, written);
        Assert.Equal(0u, result.Origin);
        Assert.Equal(1u, result.Provenance);
    }

    [Fact]
    public unsafe void PerformanceIgnoredFlagsAgreeAcrossRealAbiAndManagedResultValidation()
    {
        using NativeAutomationPolicy policy = Load();
        for (int flags = 0; flags < 8; flags++)
        {
            NativeAutomationRequest request = new()
            {
                StructSize = 16,
                Operation = 0,
                HasProfile = (byte)(flags & 1),
                HasConfirmed = (byte)((flags >> 1) & 1),
                ExternalUnknown = (byte)((flags >> 2) & 1),
                FromApplication = 1,
            };
            NativeAutomationRecord result = new() { StructSize = 12 };
            NativeErrorRecord error = NativeErrorRecord.Create();
            uint written = 999;
            uint status = policy.RawSelect(NativeAutomationPolicy.AutomationAbiVersion,
                &request, &result, &written, &error);
            NativeAutomationResult actual = NativeAutomationPolicy.ReadSelection(request, result, status, error, written);
            Assert.True(actual.IsSuccess, actual.Error?.ToString());
            Assert.Equal(new NativeAutomationSelection(AutomationValueOrigin.Profile,
                SettingValueSource.ApplicationProfile), actual.Selection);
        }
    }

    [Fact]
    public void ImpossibleHostValueTokensFailWithoutReimplementingPriority()
    {
        NativeAutomationRequest known = new() { StructSize = 16, Operation = 1, HasProfile = 1, HasConfirmed = 1 };
        NativeAutomationRequest unknown = known;
        unknown.ExternalUnknown = 1;
        NativeAutomationRequest absent = new() { StructSize = 16, Operation = 1 };
        NativeAutomationRequest unknownAbsent = absent;
        unknownAbsent.ExternalUnknown = 1;
        NativeAutomationRequest unknownOverrides = unknown;
        unknownOverrides.HasTemporary = 1;
        unknownOverrides.HasPersistent = 1;
        NativeErrorRecord success = NativeErrorRecord.Create();
        var cases = new (NativeAutomationRequest Request, NativeAutomationRecord Record)[]
        {
            (absent, new() { StructSize = 12, Origin = 0, Provenance = 1 }),
            (known, new() { StructSize = 12, Origin = 1, Provenance = 0 }),
            (unknownAbsent, new() { StructSize = 12, Origin = 1, Provenance = 0 }),
            (unknown, new() { StructSize = 12, Origin = 0, Provenance = 1 }),
            (unknownOverrides, new() { StructSize = 12, Origin = 2, Provenance = 3 }),
            (unknownOverrides, new() { StructSize = 12, Origin = 3, Provenance = 4 }),
            (known, new() { StructSize = 12, Origin = 2, Provenance = 3 }),
            (known, new() { StructSize = 12, Origin = 3, Provenance = 4 }),
            (known, new() { StructSize = 12, Origin = 0, Provenance = 99 }),
            (known, new() { StructSize = 4, Origin = 0, Provenance = 1 }),
        };
        foreach (var entry in cases)
        {
            NativeAutomationResult result = NativeAutomationPolicy.ReadSelection(
                entry.Request, entry.Record, 0, success, 0);
            Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult, result.Error!.Kind);
        }
        // Both null fallbacks are original behavior, and remain admitted.
        Assert.True(NativeAutomationPolicy.ReadSelection(absent,
            new() { StructSize = 12, Origin = 1, Provenance = 0 }, 0, success, 0).IsSuccess);
        Assert.True(NativeAutomationPolicy.ReadSelection(unknownAbsent,
            new() { StructSize = 12, Origin = 0, Provenance = 1 }, 0, success, 0).IsSuccess);
    }

    [Fact]
    public void IndependentLoaderNeedsNoCodecOrProfileExports()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_AUTOMATION_ONLY_LIBRARY")
            ?? throw new InvalidOperationException("Automation-only fixture is required.");
        NativeAutomationLoadResult result = NativeAutomationPolicy.Load(path);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        using NativeAutomationPolicy legacy = result.Policy!;
        Assert.Equal(NativeBoundaryErrorKind.MissingExport,
            legacy.CheckPerformanceSource(PerformancePreset.Balanced, PowerSource.PluggedIn).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.MissingExport,
            NativeAutomationPolicy.Load(path, requirePerformanceSourceEligibility: true).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.MissingExport, NativeProfilePolicy.Load(path).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.MissingExport, NativeReportCodec.Load(path).Error!.Kind);
        string policyPath = Environment.GetEnvironmentVariable("OPENBLADE_POLICY_ONLY_LIBRARY")!;
        Assert.Equal(NativeBoundaryErrorKind.MissingExport, NativeAutomationPolicy.Load(policyPath).Error!.Kind);
    }

    [Fact]
    public async Task ConcurrentCallsAndDisposalRetainSafeModuleLifetime()
    {
        NativeAutomationPolicy policy = Load();
        using (NativeAutomationPolicy independent = Load())
        {
            Assert.True(independent.SelectPerformance(true, true, true).IsSuccess);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                NativeAutomationResult result = policy.SelectPerformance(true, true, true);
                Assert.True(result.IsSuccess, result.Error?.ToString());
                Assert.Equal(new NativeAutomationSelection(AutomationValueOrigin.Persistent,
                    SettingValueSource.PersistentOverride), result.Selection);
            }
        })));
        Task[] calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                NativeAutomationResult result = policy.SelectLighting(true, true, true, true, true, true);
                if (result.IsSuccess)
                {
                    Assert.Equal(new NativeAutomationSelection(AutomationValueOrigin.Confirmed,
                        SettingValueSource.ConfirmedFirmwareState), result.Selection);
                }
                else
                {
                    Assert.Equal(NativeBoundaryErrorKind.Disposed, result.Error!.Kind);
                }
            }
        })).ToArray();
        policy.Dispose();
        await Task.WhenAll(calls);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, policy.SelectPerformance(false, false, false).Error!.Kind);
        policy.Dispose();
    }

    [Fact]
    public void MalformedFixturesFailClosed()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "automation-policy.json"));
        foreach (string invalid in new[]
        {
            json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal),
            json.Replace("automation-selection", "unknown", StringComparison.Ordinal),
            json.Replace(AutomationParityFixture.Revision, "unknown", StringComparison.Ordinal),
            json.Replace("\"operation\":0", "\"operation\":9", StringComparison.Ordinal),
            json.Replace("\"profile\":true", "\"profile\":\"true\"", StringComparison.Ordinal),
            json.Replace("\"profile\":true", "\"profile\":true,\"profile\":false", StringComparison.Ordinal),
            json.Replace("\"profile\":true,", "", StringComparison.Ordinal),
            json.Replace("\"origin\":0", "\"origin\":0,\"extra\":0", StringComparison.Ordinal),
            json.Replace("performance-application", "performance-power", StringComparison.Ordinal),
        })
        {
            Assert.ThrowsAny<JsonException>(() => AutomationParityFixture.Parse(invalid));
        }
    }
}
