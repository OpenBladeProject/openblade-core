using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

// RZ09-0482 Custom Maximum Fans follows product 669's own Synapse setter:
// evidence/source-reviewed-blades/2026-11-19-rz09-0482-maximum-fans.json.
public sealed partial class ExperimentalThermalExpansionTests
{
    [Fact]
    public void Blade14MaximumFans_EvidenceRecordMatchesCatalogPins()
    {
        const string relativePath = "evidence/source-reviewed-blades/2026-11-19-rz09-0482-maximum-fans.json";
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory.FullName, relativePath)));
        System.Text.Json.JsonElement root = document.RootElement;
        Assert.Equal("RZ09-0482", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("physicalValidationPerformed").GetBoolean());
        ExperimentalFeatureEvidence feature = Assert.Single(
            ExperimentalBladeModels.FeatureEvidenceFor(Model(0x029D)), item => item.Feature == "MaximumFans");
        foreach (System.Text.Json.JsonElement source in root.GetProperty("sources").EnumerateArray()
            .Where(item => item.GetProperty("id").GetString() is "669-main" or "669-policy"))
        {
            Assert.True(source.GetProperty("verifiedThisReview").GetBoolean());
            Assert.Contains($"{source.GetProperty("url").GetString()};SHA256={source.GetProperty("sha256").GetString()}",
                feature.Sources);
        }
        Assert.Contains(relativePath, feature.Sources);
    }

    [Fact]
    public async Task Blade14MaximumFans_EnablesAndDisablesInCustomWithEchoedSetterAndReadback()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2 };
        await using var port = Create(0x029D, session);
        var requested = CustomPerformanceSettings.Default with { MaximumFans = true };

        await port.SetCustomPerformanceAsync(requested, CancellationToken.None);

        Assert.True(session.MaximumFans);
        RazerCommandSpec setter = Assert.Single(session.Writes);
        Assert.Equal((0x07, 0x0F), (setter.ClassId, setter.CommandId));
        Assert.Equal(new byte[] { 2 }, setter.Arguments.ToArray());
        // 029D keeps its echoed acknowledgement; 029F/02A0 status-only ACKs are not inherited.
        Assert.False(setter.AllowVariableResponseLength);
        int setterIndex = session.Commands.IndexOf(setter);
        Assert.Contains(session.Commands.Take(setterIndex),
            command => command.ClassId == 0x07 && command.CommandId == 0x8F);
        Assert.Contains(session.Commands.Skip(setterIndex + 1),
            command => command.ClassId == 0x07 && command.CommandId == 0x8F);
        Assert.Equal(requested, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);

        await port.SetCustomPerformanceAsync(requested with { MaximumFans = false }, CancellationToken.None);
        Assert.False(session.MaximumFans);
        Assert.Equal([new byte[] { 2 }, [0]], session.Writes.Select(command => command.Arguments.ToArray()));
    }

    [Fact]
    public async Task Blade14MaximumFans_EntersCustomBeforeSettingFlag()
    {
        var session = new Session { Mode = 0, Cpu = 1, Gpu = 2 };
        await using var port = Create(0x029D, session);

        await port.SetCustomPerformanceAsync(
            CustomPerformanceSettings.Default with { MaximumFans = true }, CancellationToken.None);

        Assert.Equal(4, session.Mode);
        Assert.True(session.MaximumFans);
        Assert.Equal([(0x0D, 0x02), (0x0D, 0x02), (0x07, 0x0F)],
            session.Writes.Select(command => ((int)command.ClassId, (int)command.CommandId)));
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, 0)]
    [InlineData(PerformancePreset.Silent, 5)]
    public async Task Blade14MaximumFans_LeavingCustomClearsFlagBeforeModeSetter(PerformancePreset destination, byte mode)
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2, MaximumFans = true };
        await using var port = Create(0x029D, session);

        await port.SetPerformanceAsync(destination, CancellationToken.None);

        Assert.False(session.MaximumFans);
        Assert.Equal(mode, session.Mode);
        Assert.Equal([(0x07, 0x0F), (0x0D, 0x02), (0x0D, 0x02)],
            session.Writes.Select(command => ((int)command.ClassId, (int)command.CommandId)));
        Assert.Equal(new byte[] { 0 }, session.Writes.First().Arguments.ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public async Task Blade14MaximumFans_LeavingCustomRefusesCleanupThatWouldClearOtherOptions(byte flags)
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2, PowerModeControlFlags = flags };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Balanced, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(flags, session.PowerModeControlFlags);
        Assert.Equal(4, session.Mode);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Blade14MaximumFans_UnconfirmedCleanupStopsBeforeModeSetter()
    {
        var session = new Session { Mode = 4, Cpu = 1, Gpu = 2, MaximumFans = true, IgnoreMaximum = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Balanced, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(4, session.Mode);
        Assert.Equal(1, session.MaximumSetAttempts);
        Assert.DoesNotContain(session.Writes, command => command.ClassId == 0x0D);
    }

    [Theory]
    [InlineData(HidProtocolValidationFailure.Status)]
    [InlineData(HidProtocolValidationFailure.TransactionMismatch)]
    public async Task Blade14MaximumFans_AmbiguousSubmissionPausesWithoutReplay(
        HidProtocolValidationFailure protocolFailure)
    {
        var session = new Session { Mode = 4, MaximumAckProtocolFailure = protocolFailure };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);
        var requested = CustomPerformanceSettings.Default with { MaximumFans = true };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.MaximumSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
        Assert.Equal(1, session.MaximumSetAttempts);
    }

    [Fact]
    public async Task Blade14MaximumFans_AckWithoutApplicationPausesWithoutReplay()
    {
        var session = new Session { Mode = 4, IgnoreMaximum = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);
        var requested = CustomPerformanceSettings.Default with { MaximumFans = true };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.MaximumSetAttempts);
        Assert.False(session.MaximumFans);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("80")]
    [InlineData("")]
    [InlineData("0000")]
    public async Task Blade14MaximumFans_RejectsUnknownBitsAndWrongPayloadLengths(string hex)
    {
        var session = new Session { Mode = 4, PowerModePayloadOverride = Convert.FromHexString(hex) };
        await using var port = Create(0x029D, session);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(() =>
            port.QueryPerformanceFanStateAsync(CancellationToken.None));

        Assert.Equal(DeviceStateQuery.PerformanceAndFans, error.StateQuery);
        Assert.Equal([(0x07, 0x8F, FirmwareQueryOutcome.ContentRejected)], session.RetainedQueryFailures);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(4)]
    [InlineData(1)]
    public async Task Blade14MaximumFans_OtherPowerModeBitsBlockBeforeAnyCustomWrite(byte flags)
    {
        var session = new Session { Mode = 4, PowerModeControlFlags = flags };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);
        var desired = CustomPerformanceSettings.Default with { MaximumFans = (flags & 2) == 0 };

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(desired, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(flags, session.PowerModeControlFlags);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Blade14MaximumFans_FixedBaselineRejectsBeforeAnyWrite()
    {
        var session = new Session { Mode = 4, Fan = 1 };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default with { MaximumFans = true },
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task Blade14MaximumFans_ActiveFlagBlocksFixedTargetsBeforeWrite()
    {
        var session = new Session { Mode = 4, MaximumFans = true };
        await using var port = Create(0x029D, session);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(session.MaximumFans);
    }
}
