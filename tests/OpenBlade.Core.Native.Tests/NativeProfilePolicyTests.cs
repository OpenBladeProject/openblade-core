using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Testing;

namespace OpenBlade.Core.Native.Tests;

public sealed unsafe class NativeProfilePolicyTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static IEnumerable<object[]> Cases() => ParityFixture.Rows("profile-policy.json", "application-profile-merge", "cases");

    internal static NativeProfilePolicy Load()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_LIBRARY")
            ?? throw new InvalidOperationException("OPENBLADE_NATIVE_LIBRARY must name the actual native artifact.");
        NativePolicyLoadResult result = NativeProfilePolicy.Load(path);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Policy!;
    }

    [Fact]
    public void AllCanonicalCasesAndLayoutsAreCovered()
    {
        Assert.Equal(14, Cases().Count());
        Assert.True(NativeProfilePolicy.LayoutsMatch());
        Assert.Equal(8, Marshal.SizeOf<NativeOptionalInt>());
        Assert.Equal(12, Marshal.SizeOf<NativeOptionalUtf8>());
        Assert.Equal(44, Marshal.SizeOf<NativeDisplayProjection>());
        Assert.Equal(88, Marshal.SizeOf<NativeProfileProjection>());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CanonicalProjectionMatchesFixtureAndFrozenManagedBaseline(string id)
    {
        using NativeProfilePolicy policy = Load();
        JsonElement row = ParityFixture.Row("profile-policy.json", "application-profile-merge", "cases", id);
        Projection? basis = row.GetProperty("baseline").Deserialize<Projection>(Options);
        Projection? replacement = row.GetProperty("override").Deserialize<Projection>(Options);
        PowerProfile? baseline = basis is null ? null : ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            Performance = basis.Performance!.Value,
            Fans = basis.Fans!.Value,
            FanControl = ToFan(basis.FanControl),
            Display = basis.Display,
            LaptopBehavior = LaptopBehaviorProfileSettings.Unconfigured() with { GamingModeEnabled = basis.GamingModeEnabled },
        };
        ApplicationPowerProfileOverride? value = replacement is null ? null : new()
        {
            Performance = replacement.Performance,
            Fans = replacement.Fans,
            FanControl = ToFan(replacement.FanControl),
            Display = replacement.Display,
            GamingModeEnabled = replacement.GamingModeEnabled,
        };
        NativeProfileResult actual = policy.Apply(baseline, value);
        if (row.GetProperty("expectedError").GetString() is string expectedError)
        {
            Assert.Equal("baseline-null", expectedError);
            Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, actual.Error!.Kind);
            Assert.Equal(5u, actual.Error.Detail);
            Assert.Throws<ArgumentNullException>(() => ManagedProfilePolicyBaseline.Apply(baseline!, value));
            return;
        }
        Assert.True(actual.IsSuccess, actual.Error?.ToString());
        Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline!, value), actual.Profile);
        Projection expected = row.GetProperty("expected").Deserialize<Projection>(Options)!;
        Assert.Equal(expected, Project(actual.Profile!));
        if (value is null)
        {
            Assert.Same(baseline, actual.Profile);
        }
        else
        {
            DisplayProfileSettings displayBasis = baseline!.Display ?? DisplayProfileSettings.Unconfigured();
            NativeDisplayResult display = policy.ApplyDisplay(displayBasis, value.Display);
            Assert.True(display.IsSuccess, display.Error?.ToString());
            Assert.Equal(expected.Display, display.Display);
            Assert.Equal(ManagedProfilePolicyBaseline.ApplyDisplay(displayBasis, value.Display), display.Display);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostReconstructionPreservesOpaqueFieldsAndArbitraryCurves(bool overrideOpaqueFields)
    {
        using NativeProfilePolicy policy = Load();
        var baselineCurve = new ManualFanCurve([new(32, 2100), new(84, 5300)], [new(34, 2200), new(88, 5200)], false);
        var overrideCurve = new ManualFanCurve([new(31, 2300), new(91, 5100)], [new(36, 2400), new(92, 5400)], true);
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            FanControl = new(FanMode.ManualCurve, 3900, baselineCurve),
            CustomPerformance = new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low) { MaximumFans = true, GpuClockOffsets = new(50, 100) },
            GpuClockTuning = new(true, new(75, 150)),
            Lighting = new(LightingEffect.Breathing, new(12, 34, 56), 67) { LogoMode = LogoLightingMode.Off },
            LaptopBehavior = new(false, (FunctionKeyPrimaryMode)1, true) { GamingModeKeyPolicy = new(true, false) },
            LightingPowerPolicy = new(true, 7),
            BatteryProtectionEnabled = false,
            WindowsPowerMode = (WindowsPowerMode)2,
            // No validation is moved into this pure projection; this deliberately
            // opaque sentinel also proves that reconstruction retains this object.
            ThxAudio = new(false, null!, null!, []),
        };
        ApplicationPowerProfileOverride value = new()
        {
            Performance = PerformancePreset.Custom,
            Display = new("opaque:\0色", false, "") { BrightnessPercent = 0 },
            GamingModeEnabled = false,
            FanControl = overrideOpaqueFields ? new(FanMode.ManualCurve, 4100, overrideCurve) : null,
            CustomPerformance = overrideOpaqueFields ? new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High) : null,
            GpuClockTuning = overrideOpaqueFields ? new(false, new(125, 250)) : null,
            Lighting = overrideOpaqueFields ? new(LightingEffect.Static, new(98, 76, 54), 23) : null,
            GamingModeKeyPolicy = overrideOpaqueFields ? new(false, true) : null,
        };
        NativeProfileResult result = policy.Apply(baseline, value);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        PowerProfile actual = result.Profile!;
        Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline, value), actual);
        Assert.Same(value.CustomPerformance ?? baseline.CustomPerformance, actual.CustomPerformance);
        Assert.Same(value.GpuClockTuning ?? baseline.GpuClockTuning, actual.GpuClockTuning);
        Assert.Same(value.Lighting ?? baseline.Lighting, actual.Lighting);
        Assert.Same(value.GamingModeKeyPolicy ?? baseline.LaptopBehavior.GamingModeKeyPolicy, actual.LaptopBehavior!.GamingModeKeyPolicy);
        Assert.Same(overrideOpaqueFields ? overrideCurve : baselineCurve, actual.FanControl!.ManualCurve);
        Assert.Same(value.FanControl ?? baseline.FanControl, actual.FanControl);
        Assert.Same(baseline.LightingPowerPolicy, actual.LightingPowerPolicy);
        Assert.Same(baseline.ThxAudio, actual.ThxAudio);
        Assert.Equal(baseline.BatteryProtectionEnabled, actual.BatteryProtectionEnabled);
        Assert.Equal(baseline.WindowsPowerMode, actual.WindowsPowerMode);
        Assert.Equal(baseline.LaptopBehavior.StartupAnimationEnabled, actual.LaptopBehavior.StartupAnimationEnabled);
        Assert.Equal(baseline.LaptopBehavior.FunctionKeyPrimary, actual.LaptopBehavior.FunctionKeyPrimary);
    }

    [Fact]
    public void NullOverridePreservesNullObjectsAndIdentity()
    {
        using NativeProfilePolicy policy = Load();
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn with { Display = null, LaptopBehavior = null, FanControl = null };
        Assert.Same(baseline, policy.Apply(baseline, null).Profile);
        DisplayProfileSettings display = new("", null, "opaque\0色");
        Assert.Same(display, policy.ApplyDisplay(display, null).Display);
    }

    [Fact]
    public void InvalidValuesStayInHostValidationOrderExceptLegacySynthesis()
    {
        using NativeProfilePolicy policy = Load();
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            Performance = (PerformancePreset)(-123),
            Fans = (FanMode)456,
            Display = new("", true, "\0") { BrightnessPercent = 255 },
        };
        foreach (ApplicationPowerProfileOverride value in new ApplicationPowerProfileOverride[]
        {
            new(), new() { Performance = (PerformancePreset)999 },
            new() { Fans = (FanMode)999, FanControl = new(FanMode.Fixed, 2000, ManualFanCurve.Default) },
        })
        {
            NativeProfileResult result = policy.Apply(baseline, value);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline, value), result.Profile);
        }
        var invalidLegacy = new ApplicationPowerProfileOverride { Fans = (FanMode)999 };
        Assert.Equal("mode", Assert.Throws<ArgumentOutOfRangeException>(() => ManagedProfilePolicyBaseline.Apply(baseline, invalidLegacy)).ParamName);
        NativeProfileResult failure = policy.Apply(baseline, invalidLegacy);
        Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, failure.Error!.Kind);
        Assert.Equal(3u, failure.Error.Detail);
    }

    public static IEnumerable<object[]> OpaqueKeys()
    {
        // Runtime data avoids attribute metadata's UTF-8 serialization.
        yield return [new string((char)0xD800, 1), new string((char)0xDC00, 1)];
        yield return ["same", "SAME"];
        yield return ["", "\0"];
        yield return ["色\U0001F680", "\uD800x\uDC00"];
        yield return [new string('x', 100_000), new string('y', 100_000)];
        yield return [new string(['a', 'b']), new string(['a', 'b'])];
    }

    [Theory]
    [MemberData(nameof(OpaqueKeys))]
    public void SourceTokensPreserveExactOpaqueHostKeys(string baselineKey, string overrideKey)
    {
        using NativeProfilePolicy policy = Load();
        var baseline = new DisplayProfileSettings(baselineKey, true, overrideKey);
        var value = new DisplayProfileSettings(overrideKey, false, baselineKey);
        NativeDisplayResult result = policy.ApplyDisplay(baseline, value);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(ManagedProfilePolicyBaseline.ApplyDisplay(baseline, value), result.Display);
        Assert.Same(overrideKey, result.Display!.RefreshRateKey);
        Assert.Same(baselineKey, result.Display.ColorProfileKey);
        PowerProfile profile = ProfileDefaults.CreateSafeDefaults().PluggedIn with { Display = baseline };
        foreach (var replacement in new ApplicationPowerProfileOverride[]
        {
            new(), new() { Display = value },
            new() { Display = new(null, null, "") },
        })
        {
            NativeProfileResult merged = policy.Apply(profile, replacement);
            Assert.True(merged.IsSuccess, merged.Error?.ToString());
            Assert.Equal(ManagedProfilePolicyBaseline.Apply(profile, replacement), merged.Profile);
            if (replacement.Display is null)
            {
                Assert.Same(baseline, merged.Profile!.Display);
            }
            Assert.Same(replacement.Display?.RefreshRateKey ?? baseline.RefreshRateKey, merged.Profile!.Display!.RefreshRateKey);
            Assert.Same(replacement.Display?.ColorProfileKey ?? baseline.ColorProfileKey, merged.Profile.Display.ColorProfileKey);
        }
    }

    [Fact]
    public void PolicyLoaderNegotiatesWithoutAnyCodecExports()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_POLICY_ONLY_LIBRARY")
            ?? throw new InvalidOperationException("OPENBLADE_POLICY_ONLY_LIBRARY must name the native policy-only negotiation test shim.");
        NativeCodecLoadResult codec = NativeReportCodec.Load(path);
        Assert.Equal(NativeBoundaryErrorKind.MissingExport, codec.Error!.Kind);
        Assert.False(codec.IsSuccess);
        NativePolicyLoadResult result = NativeProfilePolicy.Load(path);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        using NativeProfilePolicy admitted = result.Policy!;
        // Work-method parity is exercised against the actual Rust artifact above.
        // This minimal shim proves that policy admission never requests codec exports.
        Assert.False(NativeProfilePolicy.Load(Path.GetFileName(path)).IsSuccess);
    }

    [Fact]
    public void MissingBaselinePrecedesUtf8AndNullOverridePreservesOpaqueUtf16()
    {
        using NativeProfilePolicy policy = Load();
        DisplayProfileSettings display = new("\uD800", null, "\uDC00");
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn with { Display = display };
        var value = new ApplicationPowerProfileOverride { Display = display };
        NativeProfileResult missingProfile = policy.Apply(null, value);
        Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, missingProfile.Error!.Kind);
        Assert.Equal(5u, missingProfile.Error.Detail);
        NativeDisplayResult missingDisplay = policy.ApplyDisplay(null, display);
        Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, missingDisplay.Error!.Kind);
        Assert.Equal(5u, missingDisplay.Error.Detail);
        Assert.Same(ManagedProfilePolicyBaseline.Apply(baseline, null), policy.Apply(baseline, null).Profile);
        Assert.Same(ManagedProfilePolicyBaseline.ApplyDisplay(display, null), policy.ApplyDisplay(display, null).Display);
        Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline, new()), policy.Apply(baseline, new()).Profile);
        policy.Dispose();
        Assert.Equal(NativeBoundaryErrorKind.Disposed, policy.Apply(baseline, null).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, policy.ApplyDisplay(display, null).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, policy.Apply(null, value).Error!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, policy.ApplyDisplay(null, display).Error!.Kind);
    }

    [Fact]
    public void IndependentCallsAndDisposalUseBoundedModuleLifetime()
    {
        using NativeProfilePolicy policy = Load();
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        Parallel.Invoke(() => Parallel.For(0, 64, _ =>
        {
            NativeProfileResult result = policy.Apply(baseline, new() { GamingModeEnabled = false });
            Assert.True(result.IsSuccess || result.Error?.Kind == NativeBoundaryErrorKind.Disposed, result.Error?.ToString());
        }), policy.Dispose);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, policy.Apply(baseline, null).Error!.Kind);
    }

    [Fact]
    public void RawPolicyCapacityFlagsUtf8AndVersionContractsAreAtomic()
    {
        using NativeProfilePolicy policy = Load();
        NativeDisplayProjection basis = NativeDisplayProjection.Empty;
        basis.RefreshRateKey = new() { Present = 1, Length = 3 };
        byte[] input = Encoding.UTF8.GetBytes("色");
        byte[] output = Enumerable.Repeat((byte)0xA5, 5).ToArray();
        NativeErrorRecord error = NativeErrorRecord.Create();
        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
        {
            foreach (uint capacity in new uint[] { 0, 1, 2, 3, 4 })
            {
                NativeDisplayProjection result = NativeDisplayProjection.Empty;
                result.BrightnessPercent = NativeOptionalInt.From(231);
                uint written = 999;
                uint status = policy.RawApplyDisplay(NativeProfilePolicy.PolicyAbiVersion, &basis, null, inputPointer, 3,
                    &result, outputPointer + 1, capacity, &written, &error);
                Assert.Equal(capacity < 3 ? 3u : 0u, status);
                Assert.Equal(3u, written);
                Assert.Equal((byte)0xA5, output[0]);
                Assert.Equal((byte)0xA5, output[4]);
                if (capacity < 3)
                {
                    Assert.Equal(231, result.BrightnessPercent.Value);
                    Assert.All(output, value => Assert.Equal((byte)0xA5, value));
                    Assert.Equal(3u, error.RequiredLength);
                }
                else
                {
                    Assert.Equal(input, output.AsSpan(1, 3).ToArray());
                }
            }
            NativeDisplayProjection rejected = NativeDisplayProjection.Empty;
            rejected.BrightnessPercent = NativeOptionalInt.From(231);
            uint failureLength = 999;
            basis.HdrEnabled = NativeOptionalInt.From(2);
            Assert.Equal(2u, policy.RawApplyDisplay(NativeProfilePolicy.PolicyAbiVersion, &basis, null, inputPointer, 3,
                &rejected, outputPointer + 1, 3, &failureLength, &error));
            Assert.Equal(3u, error.Detail);
            Assert.Equal(0u, failureLength);
            Assert.Equal(231, rejected.BrightnessPercent.Value);
            // Version admission occurs before reading any operation record.
            Assert.Equal(1u, policy.RawApplyDisplay(0, null, null, null, 0, null, null, 0, &failureLength, &error));
            basis.HdrEnabled = default;
            input[0] = 0xFF;
            Assert.Equal(2u, policy.RawApplyDisplay(NativeProfilePolicy.PolicyAbiVersion, &basis, null, inputPointer, 3,
                &rejected, outputPointer + 1, 3, &failureLength, &error));
            Assert.Equal(4u, error.Detail);
        }
    }

    [Fact]
    public void PolicyStatusInterpreterRejectsInconsistentNativeResults()
    {
        Assert.Null(NativeProfilePolicy.CheckStatus(0, NativeErrorRecord.Create(), 0, 0));
        NativeErrorRecord error = NativeErrorRecord.Create();
        error.Category = 2;
        error.Detail = 3;
        Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, NativeProfilePolicy.CheckStatus(2, error, 0, 0)!.Kind);
        Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult, NativeProfilePolicy.CheckStatus(0, error, 0, 0)!.Kind);
        error.Category = 4;
        Assert.Equal(NativeBoundaryErrorKind.InvalidNativeResult, NativeProfilePolicy.CheckStatus(4, error, 0, 0)!.Kind);
    }

    private static FanControlSettings? ToFan(FanProjection? value) => value is null ? null : new(value.Mode, value.FixedSpeedRpm, ManualFanCurve.Default);
    private static Projection Project(PowerProfile value) => new(value.Performance, value.Fans,
        value.FanControl is null ? null : new(value.FanControl.Mode, value.FanControl.FixedSpeedRpm), value.Display, value.EffectiveLaptopBehavior.GamingModeEnabled);
    private sealed record FanProjection(FanMode Mode, int FixedSpeedRpm);
    private sealed record Projection(PerformancePreset? Performance, FanMode? Fans, FanProjection? FanControl, DisplayProfileSettings? Display, bool? GamingModeEnabled);
}
