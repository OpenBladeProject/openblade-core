using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessLifecyclePreparationTests
{
    [Fact]
    public void Exact0528PreparationNeverBorrows2026Readiness()
    {
        var selected = PadlessHyperBoostLifecycleTarget.ForExactIdentity(Identity("RZ09-0528", "RZ09-05289EN4", "2.02"));
        Assert.NotNull(selected);
        Assert.Equal(PerformancePreset.Performance, selected.BaseMode);
        Assert.Equal(Rz090528PadlessLifecyclePreparation.ManifestSha256, selected.ManifestSha256);
        Assert.NotEqual(HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256, selected.ManifestSha256);
        Assert.False(selected.Readiness.LifecycleValidationReady);
        Assert.False(selected.Readiness.WriteAdmitted);
        Assert.Equal(new HyperBoostWithoutCoolingPadEvidenceGates(false, false, false, false, false, false, false, false, false, false), selected.Readiness);
        Assert.Throws<UnvalidatedCommandException>(() => BatteryValidationFactory.RequireLifecycleReadiness(selected));
    }

    [Theory]
    [InlineData("RZ09-0528", "RZ09-05289EN4", "2.03")]
    [InlineData("RZ09-0528", "other", "2.02")]
    [InlineData("unknown", "RZ09-05289EN4", "2.02")]
    public void WrongExactTargetHasNoManifestOrValidationAuthority(string model, string sku, string bios)
    {
        Assert.Null(PadlessHyperBoostLifecycleTarget.ForExactIdentity(Identity(model, sku, bios)));
        Assert.Throws<UnvalidatedCommandException>(() => BatteryValidationFactory.RequireLifecycleReadiness(null));
    }

    [Fact]
    public void IdentityFailureAndUnknownCompatibilityHaveNoFallback()
    {
        var identity = Identity("RZ09-0528", "RZ09-05289EN4", "2.02");
        Assert.Null(PadlessHyperBoostLifecycleTarget.ForExactIdentity(identity with { Failure = HidSessionFailureKind.IdentityBlocked }));
        Assert.Null(PadlessHyperBoostLifecycleTarget.ForExactIdentity(identity with { FirmwareCompatibility = HostFirmwareCompatibility.Unknown }));
    }

    [Fact]
    public void Existing2026SelectionRetainsBalancedManifestAndReadiness()
    {
        var selected = PadlessHyperBoostLifecycleTarget.ForExactIdentity(Identity("RZ09-0581", "RZ09-05819EN4", "4.00"));
        Assert.NotNull(selected);
        Assert.Equal(PerformancePreset.Balanced, selected.BaseMode);
        Assert.Equal(HyperBoostWithoutCoolingPadAdmissionSource.TrustedManifestSha256, selected.ManifestSha256);
        Assert.Equal(HyperBoostWithoutCoolingPadEvidenceGates.Current, selected.Readiness);
    }

    [Fact]
    public void PreparationHashRejectsEvenPlausibleDescriptorChanges()
    {
        Assert.Throws<UnvalidatedCommandException>(() => Rz090528PadlessLifecyclePreparation.Validate("{}"u8.ToArray()));
    }

    [Fact]
    public async Task SyntheticDeveloperCompositionUsesOnlyPerformancePairAndAllowsSafeRestoreAfterPadArrival()
    {
        var eligibility = new Pad();
        var session = new Session();
        await using var port = Create(session, eligibility);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, port.Capabilities.HyperBoostWithoutCoolingPadAccess);
        await ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default);
        eligibility.State = CoolingPadConnectionState.PresentExact;
        await port.SetHyperBoostAsync(false, PerformancePreset.Performance, default);
        Assert.Equal(new[] { "01010700", "01020700", "01010200", "01020200" }, session.Writes);
        Assert.Equal(2, session.PowerQueries);
        int count = session.Commands;
        await Assert.ThrowsAnyAsync<Exception>(() => port.SetHyperBoostAsync(true, PerformancePreset.Balanced, default));
        Assert.Equal(count, session.Commands);
    }

    [Fact]
    public async Task NormalEnableCannotUseDeveloperPadlessAuthority()
    {
        var session = new Session();
        await using var port = Create(session, new Pad());
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetHyperBoostAsync(true, PerformancePreset.Performance, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.PresentExact)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    public async Task DeveloperEntryRequiresExactAbsence(CoolingPadConnectionState state)
    {
        var session = new Session();
        await using var port = Create(session, new Pad { State = state });
        await Assert.ThrowsAnyAsync<Exception>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task WrongBaselineOrInsufficientPowerCannotSubmit()
    {
        foreach (bool wrongBaseline in new[] { true, false })
        {
            var session = new Session { InitialMode = wrongBaseline ? (byte)0 : (byte)2, FullPower = wrongBaseline };
            await using var port = Create(session, new Pad());
            await Assert.ThrowsAnyAsync<Exception>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
            Assert.Empty(session.Writes);
        }
    }

    [Fact]
    public async Task PadArrivalBetweenChannelsPoisonsWithoutReplay()
    {
        var pad = new Pad();
        var session = new Session { AfterFirstWrite = () => pad.State = CoolingPadConnectionState.PresentExact };
        await using var port = Create(session, pad);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
        int count = session.Commands;
        await Assert.ThrowsAnyAsync<Exception>(() => port.SetHyperBoostAsync(false, PerformancePreset.Performance, default));
        Assert.Equal(count, session.Commands);
    }

    [Fact]
    public async Task NativeSubmissionGateRequiresAbsenceAtSubmission()
    {
        var pad = new Pad();
        var session = new Session { BeforeWrite = () => pad.State = CoolingPadConnectionState.ObservationFailed };
        await using var port = Create(session, pad);
        await Assert.ThrowsAnyAsync<Exception>(() => ((IPadlessHyperBoostLifecycleValidationPort)port).EnablePadlessHyperBoostLifecycleValidationAsync(default));
        Assert.Empty(session.Writes);
        Assert.True(session.NativeGateRefused);
    }

    private static HostIdentityReadResult Identity(string model, string sku, string bios) =>
        new(new("Razer", "Blade 16 - " + model, sku, bios), null, HostFirmwareCompatibility.Tested);

    private static ProductionBatteryPort Create(Session session, Pad pad)
    {
        var target = new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02");
        CapturedCommandFixture Fixture(string action, byte channel, byte mode) => new(target, action, 0x0D, 0x02,
            [1, channel, mode, 0], true, true, true, true, true);
        var performance = DeviceCommandCatalog.Create(target,
            [Fixture(ProductionBatteryPort.PerformanceBoostFirstAction, 1, 2), Fixture(ProductionBatteryPort.PerformanceBoostSecondAction, 2, 2)]);
        var hyperBoost = DeviceCommandCatalog.Create(target,
            [Fixture(ProductionBatteryPort.HyperBoostEnableFirstAction, 1, 7), Fixture(ProductionBatteryPort.HyperBoostEnableSecondAction, 2, 7)]);
        return new(session, performance, BatteryValidationFactory.CreateRz090528ProductionBatteryReadEvidence(),
            new ValidationRunContext(), TimeSpan.FromSeconds(1),
            admittedAdapterPowerStates: new HashSet<AdapterPowerState> { AdapterPowerState.FullPower280W },
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite, hyperBoostWithoutCoolingPadAccess: DeviceFeatureAccess.ObservedOnly,
            hyperBoostBaseModes: new(DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite),
            hyperBoostCatalog: hyperBoost, hyperBoostEligibility: pad, allowPadlessHyperBoostLifecycleValidation: true,
            padlessHyperBoostLifecycleBaseMode: PerformancePreset.Performance);
    }

    private sealed class Pad : IHyperBoostEligibilitySource
    {
        internal CoolingPadConnectionState State = CoolingPadConnectionState.Absent;
        public CoolingPadConnectionState ObserveCoolingPad() => State;
    }

    private sealed class Session : IRazerCommandSession
    {
        internal byte InitialMode = 2;
        internal bool FullPower = true;
        internal Action? AfterFirstWrite;
        internal Action? BeforeWrite;
        internal readonly List<string> Writes = [];
        internal int Commands, PowerQueries;
        internal bool NativeGateRefused;
        private readonly Dictionary<byte, byte> modes = [];
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = assignTransactionId(); Commands++;
            byte[] input = command.Arguments.ToArray();
            byte[] response;
            if (command.CommandId == 0x8C) { PowerQueries++; response = FullPower ? [0x11, 0x11] : [0x07, 0x11]; }
            else if (command.CommandId == 0x82) { response = [1, input[1], modes.GetValueOrDefault(input[1], InitialMode), 0]; }
            else if (command.CommandId == 2)
            {
                BeforeWrite?.Invoke();
                if (command.SubmissionAllowed?.Invoke() == false)
                {
                    NativeGateRefused = true;
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked));
                }
                modes[input[1]] = input[2]; Writes.Add(Convert.ToHexString(input)); response = input;
                if (Writes.Count == 1) { AfterFirstWrite?.Invoke(); }
            }
            else { throw new InvalidOperationException("Unexpected test command"); }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        public ValueTask DisposeAsync() { Disposition = RazerCommandSessionDisposition.FullyDisposed; return ValueTask.CompletedTask; }
    }
}
