using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

/// <summary>
/// Synchronous ledger transitions for thermal convergence. Returned state is
/// proposed bookkeeping, never permission to submit a hardware write. The runtime
/// retains the arbiter, ledger/store authority, persistence and fresh admission
/// checks under its existing operation gate; no authority is cached here.
/// </summary>
internal static class ThermalConvergenceLedgerTransitions
{
    internal static ThermalConvergenceLedgerState RefreshIntent(
        ThermalConvergenceLedgerState prior,
        DesiredDeviceState desired,
        ThermalControlPolicy policy,
        ThermalPolicyUpgradeDisclosureState disclosure,
        PowerSource? currentDestination,
        string fingerprint,
        bool deviceAuthorityInvalidated,
        bool policyChanged = false)
    {
        desired = NormalizeThermalIntent(desired);
        string performanceRevision = CreateThermalRevision(new
        {
            desired.Performance,
            desired.CustomPerformance,
            desired.GpuClockTuning,
        });
        string fanRevision = CreateThermalRevision(new
        {
            desired.Fans,
            desired.FanControl,
        });
        string policyRevision = CreateThermalRevision(new
        {
            ThermalControlPolicy = policy,
            ThermalPolicyUpgradeDisclosure = disclosure,
        });

        bool intentChanged = policyChanged
            || !StringComparer.Ordinal.Equals(
                prior.PerformanceDesiredRevision,
                performanceRevision)
            || !StringComparer.Ordinal.Equals(prior.FanDesiredRevision, fanRevision)
            || !StringComparer.Ordinal.Equals(prior.PolicyRevision, policyRevision);
        // A durable in-flight reservation means a command may already have
        // reached firmware. Preserve its exact destination, contradiction,
        // revisions, and pre-write evidence until a fresh observation retires
        // it read-only. A later observation can then advance to new intent.
        if (prior.InFlight is not null)
        {
            return prior;
        }

        bool deviceChanged = prior.DeviceAuthorityEpoch == Guid.Empty
            || !StringComparer.Ordinal.Equals(prior.DeviceFingerprint, fingerprint)
            || deviceAuthorityInvalidated;
        Guid deviceEpoch = deviceChanged ? Guid.NewGuid() : prior.DeviceAuthorityEpoch;
        long generation = prior.IsEmpty || intentChanged || deviceChanged
            ? prior.IntentGeneration + 1
            : prior.IntentGeneration;

        IReadOnlyList<ThermalConvergenceTombstone> tombstones = policyChanged
                ? []
                : [.. prior.Tombstones.Where(tombstone =>
                    tombstone.Destination != currentDestination
                    || StringComparer.Ordinal.Equals(
                        tombstone.PolicyRevision,
                        policyRevision)
                        && (!tombstone.Contradiction.HasFlag(
                                ThermalConvergenceContradiction.Performance)
                            || StringComparer.Ordinal.Equals(
                                tombstone.PerformanceDesiredRevision,
                                performanceRevision))
                        && (!tombstone.Contradiction.HasFlag(
                                ThermalConvergenceContradiction.FanTopology)
                            || StringComparer.Ordinal.Equals(
                                tombstone.FanDesiredRevision,
                                fanRevision)))];
        IReadOnlyList<ThermalConvergenceBudget> budgets = policyChanged
                ? []
                : [.. prior.Budgets.Where(budget =>
                    budget.Destination != currentDestination
                    || StringComparer.Ordinal.Equals(
                        budget.PolicyRevision,
                        policyRevision)
                        && StringComparer.Ordinal.Equals(
                            budget.DesiredRevision,
                            budget.Component == ThermalComponent.Performance
                                ? performanceRevision
                                : fanRevision))];

        return prior with
        {
            DeviceAuthorityEpoch = deviceEpoch,
            DeviceFingerprint = fingerprint,
            IntentGeneration = generation,
            EffectiveIntent = desired,
            PerformanceDesiredRevision = performanceRevision,
            FanDesiredRevision = fanRevision,
            PolicyRevision = policyRevision,
            SettledDestination = currentDestination ?? prior.SettledDestination,
            Contradiction = ThermalConvergenceContradiction.None,
            Tombstones = tombstones,
            Budgets = budgets,
            TerminalDisposition = null,
            TerminalAffectedComponents = [],
        };
    }

    private static DesiredDeviceState NormalizeThermalIntent(
        DesiredDeviceState desired) => desired with
        {
            FanControl = desired.FanControl
                ?? FanControlSettings.FromLegacy(desired.Fans.Value),
        };

    private static string CreateThermalRevision<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] hash = SHA256.HashData(json);
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    internal static bool IsExpectedReservation(
        ThermalConvergenceLedgerState actual,
        ThermalConvergenceLedgerState expected)
    {
        ThermalConvergenceInFlight? actualInFlight = actual.InFlight;
        ThermalConvergenceInFlight? expectedInFlight = expected.InFlight;
        return actualInFlight is not null
            && expectedInFlight is not null
            && actualInFlight.ReconciliationRequired
            && actualInFlight.TransitionAttemptId
                == expectedInFlight.TransitionAttemptId
            && actualInFlight.DeclaredComponents.SequenceEqual(
                expectedInFlight.DeclaredComponents)
            && actualInFlight.PreWriteObservation
                == expectedInFlight.PreWriteObservation
            && actualInFlight.DesiredTarget == expectedInFlight.DesiredTarget
            && string.Equals(
                actual.ActiveUserSid,
                expected.ActiveUserSid,
                StringComparison.OrdinalIgnoreCase)
            && actual.DeviceAuthorityEpoch == expected.DeviceAuthorityEpoch
            && actual.IntentGeneration == expected.IntentGeneration
            && StringComparer.Ordinal.Equals(
                actual.DeviceFingerprint,
                expected.DeviceFingerprint)
            && actual.EffectiveIntent == expected.EffectiveIntent
            && StringComparer.Ordinal.Equals(
                actual.PerformanceDesiredRevision,
                expected.PerformanceDesiredRevision)
            && StringComparer.Ordinal.Equals(
                actual.FanDesiredRevision,
                expected.FanDesiredRevision)
            && StringComparer.Ordinal.Equals(
                actual.PolicyRevision,
                expected.PolicyRevision)
            && actual.SettledDestination == expected.SettledDestination
            && actual.Contradiction == expected.Contradiction;
    }

    internal static ThermalConvergenceLedgerState Reserve(
        ThermalConvergenceLedgerState ledger,
        ThermalConvergenceCandidate candidate,
        Guid transitionAttemptId,
        ConfirmedDeviceState fresh,
        DesiredDeviceState desired,
        IReadOnlyList<ThermalComponent> affected)
    {
        var budgets = ledger.Budgets.ToList();
        foreach (ThermalComponent component in affected)
        {
            string revision = component == ThermalComponent.Performance
                ? candidate.PerformanceDesiredRevision
                : candidate.FanDesiredRevision;
            int index = budgets.FindIndex(budget =>
                budget.Destination == candidate.Destination
                && budget.Component == component
                && budget.DesiredRevision == revision
                && budget.PolicyRevision == ledger.PolicyRevision);
            if (index >= 0)
            {
                budgets[index] = budgets[index] with
                {
                    ConsumedClaims = budgets[index].ConsumedClaims + 1,
                };
            }
            else
            {
                budgets.Add(new ThermalConvergenceBudget(
                    candidate.Destination,
                    component,
                    revision,
                    ledger.PolicyRevision!,
                    ConsumedClaims: 1,
                    MaximumClaims: 1));
            }
        }

        return ledger with
        {
            SettledDestination = candidate.Destination,
            Contradiction = candidate.Contradiction,
            InFlight = new ThermalConvergenceInFlight(
                transitionAttemptId,
                affected,
                fresh,
                desired,
                ReconciliationRequired: true),
            Budgets = [.. budgets.OrderBy(budget => budget.Destination)
                .ThenBy(budget => budget.Component)],
            TerminalDisposition = null,
            TerminalAffectedComponents = [],
        };
    }

    internal static ThermalConvergenceLedgerState Complete(
        ThermalConvergenceLedgerState ledger,
        PowerSource destination,
        ThermalConvergenceContradiction contradiction,
        IReadOnlyList<ThermalComponent> affected,
        ThermalTransitionDisposition disposition)
    {
        ThermalConvergenceTombstone tombstone = CreateThermalTombstone(
            ledger, destination, contradiction, affected, disposition);
        return ledger with
        {
            InFlight = null,
            Tombstones = [
                .. ledger.Tombstones.Where(existing => existing != tombstone),
                tombstone,
            ],
            TerminalDisposition = disposition,
            TerminalAffectedComponents = affected,
        };
    }

    private static ThermalConvergenceTombstone CreateThermalTombstone(
        ThermalConvergenceLedgerState ledger,
        PowerSource destination,
        ThermalConvergenceContradiction contradiction,
        IReadOnlyList<ThermalComponent> affected,
        ThermalTransitionDisposition disposition) => new(
            destination,
            contradiction,
            affected,
            contradiction.HasFlag(ThermalConvergenceContradiction.Performance)
                ? ledger.PerformanceDesiredRevision
                : null,
            contradiction.HasFlag(ThermalConvergenceContradiction.FanTopology)
                ? ledger.FanDesiredRevision
                : null,
            ledger.PolicyRevision!,
            disposition);
}
