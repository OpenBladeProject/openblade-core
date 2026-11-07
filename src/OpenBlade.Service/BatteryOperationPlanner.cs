using OpenBlade.Contracts;

namespace OpenBlade.Service;

// Plans contain only typed intent and readback predicates, never device access.
internal abstract record BatteryOperation
{
    internal sealed record None : BatteryOperation;
    internal sealed record ChargeLimit(BatteryPolicy Policy) : BatteryOperation;
    internal sealed record ProtectionOff : BatteryOperation;
    internal sealed record TemporaryFullCharge(bool Enabled) : BatteryOperation;
}

internal sealed record BatteryOperationPlan(
    string Name,
    bool Supported,
    bool InitiallyMatching,
    BatteryOperation Operation,
    Func<ConfirmedBatteryState?, bool> Matches)
{
    internal BatteryConfirmationScope ConfirmationScope =>
        Operation is BatteryOperation.ChargeLimit or BatteryOperation.ProtectionOff
            ? BatteryConfirmationScope.PolicyOnly : BatteryConfirmationScope.All;
}

internal static class BatteryOperationPlanner
{
    internal static BatteryOperationPlan CreateBatteryPlan(
        DesiredBatteryState desired,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        if (confirmed is null || confirmed.TemporaryFullCharge is null
            && (capabilities.SupportsTemporaryFullCharge || desired.TemporaryFullCharge))
        {
            return BlockedBatteryPlan("Battery");
        }

        desired = WithoutStaleFullCharge(desired, confirmed);

        if (confirmed.TemporaryFullCharge is bool temporary && desired.TemporaryFullCharge != temporary)
        {
            return CreateTemporaryFullChargePlan(
                desired.TemporaryFullCharge,
                capabilities,
                confirmed);
        }

        if (confirmed.TemporaryFullCharge == true)
        {
            return BatteryMatches(desired, confirmed)
                ? MatchingBatteryPlan("Battery", confirmed)
                : BlockedBatteryPlan("Battery");
        }

        if (desired.ProtectionEnabled != confirmed.ProtectionEnabled)
        {
            return CreateProtectionPlan(
                desired.ProtectionEnabled,
                desired.ConfiguredPolicy,
                capabilities,
                confirmed);
        }

        if (desired.ProtectionEnabled
            && desired.ConfiguredPolicy.ChargeLimitPercent != confirmed.ChargeLimitPercent)
        {
            return CreateChargeLimitPlan(
                desired.ConfiguredPolicy,
                capabilities,
                confirmed);
        }

        return BatteryMatches(desired, confirmed, allowUnmanagedOverride: !capabilities.SupportsTemporaryFullCharge)
            ? MatchingBatteryPlan("Battery", confirmed)
            : BlockedBatteryPlan("Battery");
    }

    internal static BatteryOperationPlan CreateChargeLimitPlan(
        BatteryPolicy value,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        bool admitted = capabilities.ChargeLimits.Contains(value.ChargeLimitPercent);
        bool exactPrecondition = confirmed is
        {
            ProtectionEnabled: true,
            ChargeLimitPercent: int currentLimit,
        } && PolicyOperationAdmitted(capabilities, confirmed)
            && capabilities.ChargeLimits.Contains(currentLimit);
        if (!admitted || !exactPrecondition)
        {
            return BlockedBatteryPlan("Charge limit");
        }

        bool Matches(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            ChargeLimitPercent: var limit,
        } && PolicyOperationAdmitted(capabilities, state) && limit == value.ChargeLimitPercent;
        return new BatteryOperationPlan(
            "Charge limit",
            Supported: true,
            InitiallyMatching: Matches(confirmed),
            new BatteryOperation.ChargeLimit(value),
            Matches);
    }

    internal static BatteryOperationPlan CreateProtectionPlan(
        bool enabled,
        BatteryPolicy storedPolicy,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        if (confirmed is { ProtectionEnabled: false } && PolicyOperationAdmitted(capabilities, confirmed)
            && !enabled)
        {
            return MatchingBatteryPlan("Battery protection", confirmed);
        }

        bool storedLimitAdmitted = capabilities.ChargeLimits.Contains(
            storedPolicy.ChargeLimitPercent);
        if (confirmed is null || enabled && !storedLimitAdmitted
            || !PolicyOperationAdmitted(capabilities, confirmed))
        {
            return BlockedBatteryPlan("Battery protection");
        }

        if (!enabled)
        {
            if (!capabilities.SupportsBatteryProtectionOff
                || confirmed is not
                {
                    ProtectionEnabled: true,
                    ChargeLimitPercent: int currentLimit,
                }
                // Protection Off has its own admitted command. The current
                // limit must be readable and supported, but need not equal
                // the saved limit used when protection is enabled again.
                || !capabilities.ChargeLimits.Contains(currentLimit))
            {
                return BlockedBatteryPlan("Battery protection");
            }

            bool MatchesOff(ConfirmedBatteryState? state) => state is
            {
                ProtectionEnabled: false,
                ChargeLimitPercent: null,
            } && PolicyOperationAdmitted(capabilities, state);
            return new BatteryOperationPlan(
                "Battery protection",
                Supported: true,
                InitiallyMatching: false,
                new BatteryOperation.ProtectionOff(),
                MatchesOff);
        }

        bool MatchesOn(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            ChargeLimitPercent: var limit,
        } && PolicyOperationAdmitted(capabilities, state) && limit == storedPolicy.ChargeLimitPercent;
        if (MatchesOn(confirmed))
        {
            return MatchingBatteryPlan("Battery protection", confirmed);
        }

        if (confirmed is not
            {
                ProtectionEnabled: false,
                ChargeLimitPercent: null,
            })
        {
            return BlockedBatteryPlan("Battery protection");
        }

        return new BatteryOperationPlan(
            "Battery protection",
            Supported: true,
            InitiallyMatching: false,
            new BatteryOperation.ChargeLimit(storedPolicy),
            MatchesOn);
    }

    // Temporary full charge is a firmware one-shot that is enabled only by an
    // explicit request. Automatic paths (transitions, reapply, policy and
    // protection changes) may keep or cancel it, but an intent that reads back
    // inactive has ended, so it is never planned as an enable.
    internal static DesiredBatteryState WithoutStaleFullCharge(
        DesiredBatteryState desired,
        ConfirmedBatteryState? confirmed) =>
        desired.TemporaryFullCharge && confirmed?.TemporaryFullCharge == false
            ? new DesiredBatteryState(
                desired.ConfiguredPolicy,
                desired.ProtectionEnabled,
                false)
            : desired;

    internal static BatteryOperationPlan CreateTemporaryFullChargePlan(
        bool enabled,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        string operationName = enabled
            ? "Temporary full charge"
            : "Temporary full charge cancellation";

        if (confirmed?.TemporaryFullCharge == enabled)
        {
            return MatchingBatteryPlan(operationName, confirmed);
        }

        if (!capabilities.SupportsTemporaryFullCharge
            || confirmed is not
            {
                ProtectionEnabled: true,
                ChargeLimitPercent: int currentLimit,
                TemporaryFullCharge: not null,
            }
            || !capabilities.ChargeLimits.Contains(currentLimit))
        {
            return BlockedBatteryPlan(operationName);
        }

        bool Matches(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            ChargeLimitPercent: var limit,
            TemporaryFullCharge: var temporary,
        } && limit == currentLimit && temporary == enabled;
        return new BatteryOperationPlan(
            operationName,
            Supported: true,
            InitiallyMatching: false,
            new BatteryOperation.TemporaryFullCharge(enabled),
            Matches);
    }

    private static BatteryOperationPlan MatchingBatteryPlan(
        string name,
        ConfirmedBatteryState confirmed) => new(
            name,
            Supported: true,
            InitiallyMatching: true,
            new BatteryOperation.None(),
            state => state == confirmed);

    private static BatteryOperationPlan BlockedBatteryPlan(string name) => new(
        name,
        Supported: false,
        InitiallyMatching: false,
        new BatteryOperation.None(),
        _ => false);

    // A limiter-only backend has no override command to coordinate. Its unknown
    // flag stays unknown; an admitted override backend still needs a real Off read.
    private static bool PolicyOperationAdmitted(DeviceCapabilities capabilities, ConfirmedBatteryState? state) =>
        state is not null && (state.TemporaryFullCharge == false
            || state.TemporaryFullCharge is null && !capabilities.SupportsTemporaryFullCharge);

    internal static bool BatteryMatches(
        DesiredBatteryState desired,
        ConfirmedBatteryState? confirmed,
        bool allowUnmanagedOverride = false) => confirmed is not null
        && desired.ProtectionEnabled == confirmed.ProtectionEnabled
        && (!desired.ProtectionEnabled
            || desired.ConfiguredPolicy.ChargeLimitPercent == confirmed.ChargeLimitPercent)
        && (desired.TemporaryFullCharge == confirmed.TemporaryFullCharge
            || allowUnmanagedOverride && !desired.TemporaryFullCharge && confirmed.TemporaryFullCharge is null);
}
