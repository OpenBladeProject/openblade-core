using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ThermalConvergenceLedgerTransitionsTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private const string Fingerprint = "OpenBlade:RZ09-0581";

    [Fact]
    public void RefreshIntent_InFlightPreservesExactReservationAcrossAuthorityChanges()
    {
        ThermalConvergenceLedgerState prior = CreateReservedState();
        DesiredDeviceState changed = CreateDesired(PerformancePreset.Performance);

        ThermalConvergenceLedgerState refreshed =
            ThermalConvergenceLedgerTransitions.RefreshIntent(
                prior,
                changed,
                ThermalControlPolicy.Strict,
                ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending,
                PowerSource.Battery,
                "OpenBlade:replacement",
                deviceAuthorityInvalidated: true,
                policyChanged: true);

        Assert.Same(prior, refreshed);
    }

    [Fact]
    public void RefreshIntent_AdvancesOnlyChangedIntentOrDeviceAuthority()
    {
        DesiredDeviceState desired = CreateDesired();
        ThermalConvergenceLedgerState prior = CreateSettledState(desired);

        ThermalConvergenceLedgerState unchanged = Refresh(prior, desired);
        Assert.Equal(prior.IntentGeneration, unchanged.IntentGeneration);
        Assert.Equal(prior.DeviceAuthorityEpoch, unchanged.DeviceAuthorityEpoch);
        Assert.Equal(prior.PerformanceDesiredRevision, unchanged.PerformanceDesiredRevision);
        Assert.Equal(prior.FanDesiredRevision, unchanged.FanDesiredRevision);

        ThermalConvergenceLedgerState changed = Refresh(
            prior,
            CreateDesired(PerformancePreset.Performance));
        Assert.Equal(prior.IntentGeneration + 1, changed.IntentGeneration);
        Assert.Equal(prior.DeviceAuthorityEpoch, changed.DeviceAuthorityEpoch);
        Assert.NotEqual(prior.PerformanceDesiredRevision, changed.PerformanceDesiredRevision);
        Assert.Equal(prior.FanDesiredRevision, changed.FanDesiredRevision);

        ThermalConvergenceLedgerState invalidated =
            ThermalConvergenceLedgerTransitions.RefreshIntent(
                prior,
                desired,
                ThermalControlPolicy.MaintainDesiredState,
                ThermalPolicyUpgradeDisclosureState.NotRequired,
                PowerSource.PluggedIn,
                Fingerprint,
                deviceAuthorityInvalidated: true);
        Assert.Equal(prior.IntentGeneration + 1, invalidated.IntentGeneration);
        Assert.NotEqual(prior.DeviceAuthorityEpoch, invalidated.DeviceAuthorityEpoch);
        Assert.Equal(prior.PerformanceDesiredRevision, invalidated.PerformanceDesiredRevision);
        Assert.Equal(prior.FanDesiredRevision, invalidated.FanDesiredRevision);
    }

    [Fact]
    public void RefreshIntent_PrunesOnlyCurrentDestinationEvidenceForChangedComponent()
    {
        DesiredDeviceState desired = CreateDesired();
        ThermalConvergenceLedgerState prior = CreateSettledState(desired);
        string oldPerformanceRevision = prior.PerformanceDesiredRevision!;
        string fanRevision = prior.FanDesiredRevision!;
        string policyRevision = prior.PolicyRevision!;
        prior = prior with
        {
            Tombstones =
            [
                Tombstone(PowerSource.PluggedIn, ThermalConvergenceContradiction.Performance,
                    oldPerformanceRevision, null, policyRevision),
                Tombstone(PowerSource.PluggedIn, ThermalConvergenceContradiction.FanTopology,
                    null, fanRevision, policyRevision),
                Tombstone(PowerSource.Battery, ThermalConvergenceContradiction.Performance,
                    oldPerformanceRevision, null, policyRevision),
                Tombstone(PowerSource.PluggedIn,
                    ThermalConvergenceContradiction.Performance |
                        ThermalConvergenceContradiction.FanTopology,
                    oldPerformanceRevision, fanRevision, policyRevision),
            ],
            Budgets =
            [
                Budget(PowerSource.PluggedIn, ThermalComponent.Performance,
                    oldPerformanceRevision, policyRevision),
                Budget(PowerSource.PluggedIn, ThermalComponent.Fans,
                    fanRevision, policyRevision),
                Budget(PowerSource.Battery, ThermalComponent.Performance,
                    oldPerformanceRevision, policyRevision),
            ],
        };

        ThermalConvergenceLedgerState refreshed = Refresh(
            prior,
            CreateDesired(PerformancePreset.Performance));

        Assert.Equal(2, refreshed.Tombstones.Count);
        Assert.Contains(refreshed.Tombstones, tombstone =>
            tombstone.Destination == PowerSource.PluggedIn
            && tombstone.Contradiction == ThermalConvergenceContradiction.FanTopology);
        Assert.Contains(refreshed.Tombstones, tombstone =>
            tombstone.Destination == PowerSource.Battery
            && tombstone.Contradiction == ThermalConvergenceContradiction.Performance);
        Assert.Equal(2, refreshed.Budgets.Count);
        Assert.Contains(refreshed.Budgets, budget =>
            budget.Destination == PowerSource.PluggedIn
            && budget.Component == ThermalComponent.Fans);
        Assert.Contains(refreshed.Budgets, budget =>
            budget.Destination == PowerSource.Battery
            && budget.Component == ThermalComponent.Performance);
    }

    [Fact]
    public void RefreshIntent_PolicyBoundaryClearsAllSuppressionEvidence()
    {
        ThermalConvergenceLedgerState prior = CreateSettledState(CreateDesired());
        prior = prior with
        {
            Tombstones =
            [
                Tombstone(
                    PowerSource.Battery,
                    ThermalConvergenceContradiction.Performance,
                    prior.PerformanceDesiredRevision,
                    null,
                    prior.PolicyRevision!),
            ],
            Budgets =
            [
                Budget(
                    PowerSource.Battery,
                    ThermalComponent.Performance,
                    prior.PerformanceDesiredRevision!,
                    prior.PolicyRevision!),
            ],
            Contradiction = ThermalConvergenceContradiction.Performance,
            TerminalDisposition = ThermalTransitionDisposition.External,
            TerminalAffectedComponents = [ThermalComponent.Performance],
        };

        ThermalConvergenceLedgerState refreshed =
            ThermalConvergenceLedgerTransitions.RefreshIntent(
                prior,
                CreateDesired(),
                ThermalControlPolicy.Strict,
                ThermalPolicyUpgradeDisclosureState.NotRequired,
                PowerSource.PluggedIn,
                Fingerprint,
                deviceAuthorityInvalidated: false,
                policyChanged: true);

        Assert.Empty(refreshed.Tombstones);
        Assert.Empty(refreshed.Budgets);
        Assert.Equal(ThermalConvergenceContradiction.None, refreshed.Contradiction);
        Assert.Null(refreshed.TerminalDisposition);
        Assert.Empty(refreshed.TerminalAffectedComponents);
    }

    [Fact]
    public void Reserve_ConsumesMatchingBudgetsAndPreservesOtherKeys()
    {
        DesiredDeviceState desired = CreateDesired();
        ThermalConvergenceLedgerState ledger = CreateSettledState(desired);
        ledger = ledger with
        {
            TerminalDisposition = ThermalTransitionDisposition.External,
            TerminalAffectedComponents = [ThermalComponent.Performance],
            Budgets =
            [
                Budget(
                    PowerSource.Battery,
                    ThermalComponent.Fans,
                    ledger.FanDesiredRevision!,
                    ledger.PolicyRevision!),
                Budget(
                    PowerSource.PluggedIn,
                    ThermalComponent.Performance,
                    ledger.PerformanceDesiredRevision!,
                    ledger.PolicyRevision!,
                    consumedClaims: 0),
            ],
        };
        ThermalConvergenceCandidate candidate = CreateCandidate(
            ledger,
            ThermalConvergenceContradiction.Performance |
                ThermalConvergenceContradiction.FanTopology);
        Guid attemptId = Guid.NewGuid();
        ConfirmedDeviceState fresh = CreateConfirmed(PerformancePreset.Performance);

        ThermalConvergenceLedgerState reserved =
            ThermalConvergenceLedgerTransitions.Reserve(
                ledger,
                candidate,
                attemptId,
                fresh,
                desired,
                [ThermalComponent.Performance, ThermalComponent.Fans]);

        Assert.Equal(PowerSource.PluggedIn, reserved.SettledDestination);
        Assert.Equal(candidate.Contradiction, reserved.Contradiction);
        Assert.Equal(attemptId, reserved.InFlight?.TransitionAttemptId);
        Assert.Equal(fresh, reserved.InFlight?.PreWriteObservation);
        Assert.Equal(desired, reserved.InFlight?.DesiredTarget);
        Assert.True(reserved.InFlight?.ReconciliationRequired);
        Assert.Null(reserved.TerminalDisposition);
        Assert.Empty(reserved.TerminalAffectedComponents);
        Assert.Equal(3, reserved.Budgets.Count);
        Assert.Contains(reserved.Budgets, budget =>
            budget.Destination == PowerSource.Battery
            && budget.Component == ThermalComponent.Fans
            && budget.ConsumedClaims == 1);
        Assert.Contains(reserved.Budgets, budget =>
            budget.Destination == PowerSource.PluggedIn
            && budget.Component == ThermalComponent.Performance
            && budget.ConsumedClaims == 1);
        Assert.Contains(reserved.Budgets, budget =>
            budget.Destination == PowerSource.PluggedIn
            && budget.Component == ThermalComponent.Fans
            && budget.ConsumedClaims == 1);
    }

    [Fact]
    public void Complete_CreatesComponentScopedTombstoneAndRetiresReservation()
    {
        ThermalConvergenceLedgerState reserved = CreateReservedState();
        ThermalConvergenceTombstone retained = Tombstone(
            PowerSource.Battery,
            ThermalConvergenceContradiction.FanTopology,
            null,
            reserved.FanDesiredRevision,
            reserved.PolicyRevision!);
        ThermalConvergenceBudget budget = Budget(
            PowerSource.Battery,
            ThermalComponent.Fans,
            reserved.FanDesiredRevision!,
            reserved.PolicyRevision!);
        reserved = reserved with
        {
            Tombstones = [retained],
            Budgets = [.. reserved.Budgets, budget],
        };

        ThermalConvergenceLedgerState completed =
            ThermalConvergenceLedgerTransitions.Complete(
                reserved,
                PowerSource.PluggedIn,
                ThermalConvergenceContradiction.Performance,
                [ThermalComponent.Performance],
                ThermalTransitionDisposition.External);

        Assert.Null(completed.InFlight);
        Assert.Contains(retained, completed.Tombstones);
        Assert.Contains(budget, completed.Budgets);
        Assert.Equal(2, completed.Tombstones.Count);
        ThermalConvergenceTombstone tombstone = Assert.Single(
            completed.Tombstones,
            candidate => candidate.Destination == PowerSource.PluggedIn);
        Assert.Equal(PowerSource.PluggedIn, tombstone.Destination);
        Assert.Equal(
            ThermalConvergenceContradiction.Performance,
            tombstone.Contradiction);
        Assert.Equal([ThermalComponent.Performance], tombstone.AffectedComponents);
        Assert.Equal(reserved.PerformanceDesiredRevision, tombstone.PerformanceDesiredRevision);
        Assert.Null(tombstone.FanDesiredRevision);
        Assert.Equal(reserved.PolicyRevision, tombstone.PolicyRevision);
        Assert.Equal(ThermalTransitionDisposition.External, tombstone.TerminalDisposition);
        Assert.Equal(ThermalTransitionDisposition.External, completed.TerminalDisposition);
        Assert.Equal([ThermalComponent.Performance], completed.TerminalAffectedComponents);
    }

    [Fact]
    public void IsExpectedReservation_RejectsChangedReservationIdentity()
    {
        ThermalConvergenceLedgerState expected = CreateReservedState();
        Assert.True(ThermalConvergenceLedgerTransitions.IsExpectedReservation(
            expected with { ActiveUserSid = UserSid.ToLowerInvariant() },
            expected));

        Assert.False(IsExpected(expected with { InFlight = null }, expected));
        Assert.False(IsExpected(expected with { ActiveUserSid = "S-1-5-21-9" }, expected));
        Assert.False(IsExpected(expected with
        {
            InFlight = expected.InFlight! with { TransitionAttemptId = Guid.NewGuid() },
        }, expected));
        Assert.False(IsExpected(expected with
        {
            InFlight = expected.InFlight! with { ReconciliationRequired = false },
        }, expected));
        Assert.False(IsExpected(expected with { DeviceAuthorityEpoch = Guid.NewGuid() }, expected));
        Assert.False(IsExpected(expected with { IntentGeneration = expected.IntentGeneration + 1 }, expected));
        Assert.False(IsExpected(expected with { DeviceFingerprint = "OpenBlade:other" }, expected));
        Assert.False(IsExpected(expected with { PerformanceDesiredRevision = Guid.NewGuid().ToString("D") }, expected));
        Assert.False(IsExpected(expected with { FanDesiredRevision = Guid.NewGuid().ToString("D") }, expected));
        Assert.False(IsExpected(expected with { PolicyRevision = Guid.NewGuid().ToString("D") }, expected));
        Assert.False(IsExpected(expected with { SettledDestination = PowerSource.Battery }, expected));
        Assert.False(IsExpected(expected with
        {
            Contradiction = ThermalConvergenceContradiction.FanTopology,
        }, expected));
    }

    private static bool IsExpected(
        ThermalConvergenceLedgerState actual,
        ThermalConvergenceLedgerState expected) =>
        ThermalConvergenceLedgerTransitions.IsExpectedReservation(actual, expected);

    private static ThermalConvergenceLedgerState CreateSettledState(
        DesiredDeviceState desired)
    {
        ThermalConvergenceLedgerState empty = ThermalConvergenceLedgerState.Empty(UserSid);
        return ThermalConvergenceLedgerTransitions.RefreshIntent(
            empty,
            desired,
            ThermalControlPolicy.MaintainDesiredState,
            ThermalPolicyUpgradeDisclosureState.NotRequired,
            PowerSource.PluggedIn,
            Fingerprint,
            deviceAuthorityInvalidated: false);
    }

    private static ThermalConvergenceLedgerState CreateReservedState()
    {
        DesiredDeviceState desired = CreateDesired();
        ThermalConvergenceLedgerState ledger = CreateSettledState(desired);
        return ThermalConvergenceLedgerTransitions.Reserve(
            ledger,
            CreateCandidate(ledger, ThermalConvergenceContradiction.Performance),
            Guid.NewGuid(),
            CreateConfirmed(PerformancePreset.Performance),
            desired,
            [ThermalComponent.Performance]);
    }

    private static ThermalConvergenceLedgerState Refresh(
        ThermalConvergenceLedgerState prior,
        DesiredDeviceState desired) =>
        ThermalConvergenceLedgerTransitions.RefreshIntent(
            prior,
            desired,
            ThermalControlPolicy.MaintainDesiredState,
            ThermalPolicyUpgradeDisclosureState.NotRequired,
            PowerSource.PluggedIn,
            Fingerprint,
            deviceAuthorityInvalidated: false);

    private static ThermalConvergenceCandidate CreateCandidate(
        ThermalConvergenceLedgerState ledger,
        ThermalConvergenceContradiction contradiction) => new(
            UserSid,
            AdmissionGeneration: 3,
            PowerSource.PluggedIn,
            ledger.DeviceAuthorityEpoch.ToString("D"),
            ledger.PerformanceDesiredRevision!,
            ledger.FanDesiredRevision!,
            contradiction);

    private static ThermalConvergenceTombstone Tombstone(
        PowerSource destination,
        ThermalConvergenceContradiction contradiction,
        string? performanceRevision,
        string? fanRevision,
        string policyRevision) => new(
            destination,
            contradiction,
            contradiction.HasFlag(ThermalConvergenceContradiction.Performance)
                ? contradiction.HasFlag(ThermalConvergenceContradiction.FanTopology)
                    ? [ThermalComponent.Performance, ThermalComponent.Fans]
                    : [ThermalComponent.Performance]
                : [ThermalComponent.Fans],
            performanceRevision,
            fanRevision,
            policyRevision,
            ThermalTransitionDisposition.External);

    private static ThermalConvergenceBudget Budget(
        PowerSource destination,
        ThermalComponent component,
        string desiredRevision,
        string policyRevision,
        int consumedClaims = 1) => new(
            destination,
            component,
            desiredRevision,
            policyRevision,
            consumedClaims,
            MaximumClaims: 1);

    private static DesiredDeviceState CreateDesired(
        PerformancePreset performance = PerformancePreset.Balanced)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(
                performance,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(
                FanMode.Automatic,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                defaults.PluggedIn.Lighting,
                SettingValueSource.PowerProfile),
            new DesiredBatteryState(
                defaults.BatteryPolicy,
                protectionEnabled: true,
                temporaryFullCharge: false))
        {
            FanControl = FanControlSettings.FromLegacy(FanMode.Automatic),
        };
    }

    private static ConfirmedDeviceState CreateConfirmed(
        PerformancePreset performance) => new(
            performance,
            FanMode.Automatic,
            Battery: null,
            Lighting: null);
}
