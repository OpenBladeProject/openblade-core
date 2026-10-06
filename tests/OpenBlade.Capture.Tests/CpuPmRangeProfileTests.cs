using System.Text;
using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmRangeProfileTests
{
    private static readonly string Source = new('a', 40), Manifest = new('A', 64);
    private const string Run = "11111111111111111111111111111111";
    private static byte[] Preset(int version, string? profile) => JsonSerializer.SerializeToUtf8Bytes(
        profile is null ? new { version, source = Source, manifest = Manifest } as object
            : new { version, source = Source, manifest = Manifest, profile });

    [Fact]
    public void OnlyStrictVersionTwoPresetBindsRangeProfile()
    {
        Assert.Equal(CpuPmObservationProfile.IdleThreeSamples, CpuPmOuterLaunchSchema.ReadPreset(Preset(1, null)).Profile);
        Assert.Equal(CpuPmObservationProfile.RangeSingleSample, CpuPmOuterLaunchSchema.ReadPreset(Preset(2, "RangeSingleSample")).Profile);
        foreach (byte[] bytes in new[] { Preset(2, null), Preset(1, "RangeSingleSample"), Preset(3, "RangeSingleSample"),
            Preset(2, "IdleThreeSamples"), Preset(2, "rangesinglesample"), Preset(2, "") })
        { Assert.ThrowsAny<Exception>(() => CpuPmOuterLaunchSchema.ReadPreset(bytes)); }
        string valid = Encoding.UTF8.GetString(Preset(2, "RangeSingleSample"));
        Assert.ThrowsAny<Exception>(() => CpuPmOuterLaunchSchema.ReadPreset(Encoding.UTF8.GetBytes(valid.Insert(1, "\"profile\":\"RangeSingleSample\","))));
        Assert.ThrowsAny<Exception>(() => CpuPmOuterLaunchSchema.ReadPreset(Encoding.UTF8.GetBytes(valid.Insert(1, "\"count\":1,"))));
    }

    [Fact]
    public void CommandScopeAndPinnedPresetMustMatch()
    {
        string[] old = [Rz090528CpuPmObservationProbe.TransportApproval, Rz090528CpuPmObservationProbe.LifetimeApproval, "--run-id", Run];
        string[] range = [.. old, Rz090528CpuPmObservationProbe.RangeApproval];
        var oldArguments = Rz090528CpuPmObservationProbe.Parse(old)!;
        var rangeArguments = Rz090528CpuPmObservationProbe.Parse(range, CpuPmObservationProfile.RangeSingleSample)!;
        Assert.Null(Rz090528CpuPmObservationProbe.Parse(range));
        Assert.Null(Rz090528CpuPmObservationProbe.Parse(old, CpuPmObservationProfile.RangeSingleSample));
        Assert.Null(Rz090528CpuPmObservationProbe.Parse([.. range, Rz090528CpuPmObservationProbe.RangeApproval], CpuPmObservationProfile.RangeSingleSample));
        var oldPreset = CpuPmOuterLaunchSchema.ReadPreset(Preset(1, null));
        var rangePreset = CpuPmOuterLaunchSchema.ReadPreset(Preset(2, "RangeSingleSample"));
        Assert.True(Rz090528CpuPmObservationProbe.MatchesReviewedProfile(oldArguments, oldPreset));
        Assert.True(Rz090528CpuPmObservationProbe.MatchesReviewedProfile(rangeArguments, rangePreset));
        Assert.False(Rz090528CpuPmObservationProbe.MatchesReviewedProfile(oldArguments, rangePreset));
        Assert.False(Rz090528CpuPmObservationProbe.MatchesReviewedProfile(rangeArguments, oldPreset));
    }
}
