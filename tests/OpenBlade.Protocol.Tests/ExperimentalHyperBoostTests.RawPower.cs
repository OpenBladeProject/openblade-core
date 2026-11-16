using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x029F, false)]
    [InlineData(0x029F, true)]
    [InlineData(0x029D, false)]
    [InlineData(0x029D, true)]
    public async Task RawExternalHyperBoost_FreshFullAdapterSupportsEntryAndNonBalancedExit(int product, bool padPresent)
    {
        var session = new Session { Mode = 5, HyperBoostFirmware = [1, 5, 0, 0] };
        var pad = new Pad { State = padPresent ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent };
        await using var port = HyperBoostPort(session, pad, pid: product, power: () => PowerSource.ExternalUnknown);
        await port.SetHyperBoostAsync(true, PerformancePreset.Silent, padPresent, default);
        Assert.Equal((byte)7, session.Mode);
        await port.SetHyperBoostAsync(false, PerformancePreset.Silent, padPresent, default);
        Assert.Equal((byte)5, session.Mode);
        Assert.Equal(4, session.Writes.Count());
    }

    [Theory]
    [InlineData(0x029F, false, PerformancePreset.Balanced, 0x07)]
    [InlineData(0x029F, true, PerformancePreset.Balanced, 0x0C)]
    [InlineData(0x029F, false, PerformancePreset.Silent, 0x07)]
    [InlineData(0x029F, true, PerformancePreset.Silent, 0x0C)]
    [InlineData(0x029D, false, PerformancePreset.Balanced, 0x07)]
    [InlineData(0x029D, true, PerformancePreset.Balanced, 0x0C)]
    [InlineData(0x029D, false, PerformancePreset.Silent, 0x07)]
    [InlineData(0x029D, true, PerformancePreset.Silent, 0x0C)]
    public async Task RawExternalHyperBoost_LimitedAdapterSupportsEntryAndDirectExit(
        int product, bool padPresent, PerformancePreset destination, byte actual)
    {
        byte expectedMode = destination == PerformancePreset.Silent ? (byte)5 : (byte)0;
        var session = new Session
        {
            Mode = expectedMode,
            AdapterPowerPayload = [actual, 0x11],
            HyperBoostFirmware = [1, 5, 0, 0],
        };
        var pad = new Pad { State = padPresent ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent };
        await using var port = HyperBoostPort(session, pad, pid: product, power: () => PowerSource.ExternalUnknown);
        await port.SetHyperBoostAsync(true, destination, padPresent, default);
        Assert.Equal((byte)7, session.Mode);
        await port.SetHyperBoostAsync(false, destination, padPresent, default);
        Assert.Equal(expectedMode, session.Mode);
        Assert.Equal((byte)0, session.Fan);
        Assert.Equal(4, session.Writes.Count());
    }

    [Theory]
    [InlineData(0x029F, false)]
    [InlineData(0x029F, true)]
    [InlineData(0x029D, false)]
    [InlineData(0x029D, true)]
    public async Task RawExternalHyperBoost_UnknownFailedMalformedOrContradictoryAdapterNeverAuthorizesSet(int product, bool enabled)
    {
        foreach (byte[]? adapter in new byte[]?[] { [0xFE, 0x11], [0x11], [0, 0x11], null })
        {
            var session = new Session { Mode = enabled ? (byte)0 : (byte)7, HyperBoostFirmware = [1, 5, 0, 0] };
            session.AdapterPowerPayload = adapter ?? [0x11, 0x11];
            if (adapter is null)
            {
                session.BeforeSubmission = command =>
                {
                    if (command.ClassId == 0x07 && command.CommandId == 0x8C) { throw new IOException("Adapter query failed."); }
                };
            }
            var policy = new ExperimentalFeaturesPolicy(Model(product));
            await using var port = HyperBoostPort(session, new Pad(), policy, pid: product, power: () => PowerSource.ExternalUnknown);
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetHyperBoostAsync(enabled, PerformancePreset.Balanced, true, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.True(policy.WritesAllowed);
            Assert.Empty(session.Writes);
        }
    }

    [Theory]
    [InlineData(0x029F, true, 1)]
    [InlineData(0x029F, true, 2)]
    [InlineData(0x029F, false, 1)]
    [InlineData(0x029F, false, 2)]
    [InlineData(0x029D, true, 1)]
    [InlineData(0x029D, true, 2)]
    [InlineData(0x029D, false, 1)]
    [InlineData(0x029D, false, 2)]
    public async Task RawExternalHyperBoost_OriginalWindowsSourceMustRemainUnchangedAtEachSelector(
        int product, bool enabled, int changedAt)
    {
        PowerSource raw = PowerSource.ExternalUnknown;
        var session = new Session { Mode = enabled ? (byte)0 : (byte)7, HyperBoostFirmware = [1, 5, 0, 0] };
        int attempts = 0;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x02 && ++attempts == changedAt) { raw = PowerSource.PluggedIn; }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = HyperBoostPort(session, new Pad(), policy, pid: product, power: () => raw);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetHyperBoostAsync(enabled, PerformancePreset.Balanced, true, default));
        Assert.Equal(changedAt == 1 ? WriteSubmissionState.KnownNotApplied : WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(changedAt - 1, session.Writes.Count());
        Assert.Equal(changedAt == 1, policy.WritesAllowed);
    }
}
