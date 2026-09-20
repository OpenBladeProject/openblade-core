using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

/// <summary>
/// Exact fan-intent bookkeeping. Outcome decisions never authorize a write:
/// the runtime retains live admission, continuity counters and curve targets.
/// </summary>
internal readonly record struct ExactFanControlOwnership(
    ExactFanControlOwnershipDisposition Disposition,
    FanControlSettings? Settings,
    long AuthorityEpoch,
    long DesiredRevision)
{
    internal static ExactFanControlOwnership Unowned { get; } = new(
        ExactFanControlOwnershipDisposition.Unowned,
        null,
        AuthorityEpoch: -1,
        DesiredRevision: -1);

    internal static ExactFanControlOwnership SubmissionUnresolved { get; } = new(
        ExactFanControlOwnershipDisposition.SubmissionUnresolved,
        null,
        AuthorityEpoch: -1,
        DesiredRevision: -1);

    internal static ExactFanControlOwnership ManualTopologyOnly { get; } = new(
        ExactFanControlOwnershipDisposition.ManualTopologyOnly,
        null,
        AuthorityEpoch: -1,
        DesiredRevision: -1);

    internal static ExactFanControlOwnership Owned(
        FanControlSettings settings,
        long authorityEpoch,
        long desiredRevision) => new(
            ExactFanControlOwnershipDisposition.OwnedExactIntent,
            settings,
            authorityEpoch,
            desiredRevision);

    internal bool Matches(
        FanControlSettings settings,
        long authorityEpoch,
        long desiredRevision) =>
        Disposition == ExactFanControlOwnershipDisposition.OwnedExactIntent
        && Settings is { } ownedSettings
        && ownedSettings == settings
        && AuthorityEpoch == authorityEpoch
        && DesiredRevision == desiredRevision;

    internal ExactFanOwnershipTransition ObserveTopology(FanMode? observed)
    {
        if (observed == FanMode.Automatic)
        {
            return new(Unowned, ClearCurveTargets: true);
        }

        if (observed is { } fans
            && DeviceSubsystemOrder.IsManualFanMode(fans)
            && Disposition != ExactFanControlOwnershipDisposition.OwnedExactIntent)
        {
            return new(ManualTopologyOnly, ClearCurveTargets: true);
        }

        return default;
    }

    internal ExactFanOwnershipTransition EvaluateOutcome(
        FanMode requestedMode,
        ControlResultStatus status,
        bool writeAttempted,
        bool readbackMatched,
        bool allowFanOwnershipRearm = false,
        WriteSubmissionState? writeSubmissionState = null,
        bool confirmationUnavailable = false)
    {
        bool confirmedAutomatic = requestedMode == FanMode.Automatic
            && (status == ControlResultStatus.Unchanged
                || status == ControlResultStatus.Applied
                    && readbackMatched);
        if (confirmedAutomatic)
        {
            return new(Unowned, ClearCurveTargets: true);
        }

        if (writeSubmissionState == WriteSubmissionState.Indeterminate
                && (writeAttempted || confirmationUnavailable)
            || confirmationUnavailable
                && writeAttempted
                && status != ControlResultStatus.Applied)
        {
            return new(SubmissionUnresolved, ClearCurveTargets: true);
        }

        if (status == ControlResultStatus.Unchanged)
        {
            return readbackMatched ? ObserveTopology(requestedMode) : default;
        }

        if (status != ControlResultStatus.Applied || !writeAttempted)
        {
            return default;
        }

        if (!readbackMatched)
        {
            return new(SubmissionUnresolved, ClearCurveTargets: true);
        }

        if (Disposition == ExactFanControlOwnershipDisposition.SubmissionUnresolved
            && !allowFanOwnershipRearm)
        {
            return default;
        }

        // The runtime must still perform its current live controller check.
        // Keep the prior state until that admission establishes or rejects ownership.
        return new(RequiresLiveAdmission: true);
    }
}

// Null ownership means leave the runtime field untouched, including on no-op outcomes.
internal readonly record struct ExactFanOwnershipTransition(
    ExactFanControlOwnership? Ownership = null,
    bool ClearCurveTargets = false,
    bool RequiresLiveAdmission = false);

internal enum ExactFanControlOwnershipDisposition
{
    Unowned,
    ManualTopologyOnly,
    OwnedExactIntent,
    SubmissionUnresolved,
}
