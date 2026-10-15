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
    Func<ConfirmedBatteryState?, bool> Matches);

internal static class BatteryOperationPlanner
{
    internal static BatteryOperationPlan CreateBatteryPlan(
        DesiredBatteryState desired,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        if (confirmed?.TemporaryFullCharge is null)
        {
            return BlockedBatteryPlan("Battery");
        }

        desired = WithoutStaleFullCharge(desired, confirmed);

        if (desired.TemporaryFullCharge != confirmed.TemporaryFullCharge.Value)
        {
            return CreateTemporaryFullChargePlan(
                desired.TemporaryFullCharge,
                capabilities,
                confirmed);
        }

        if (confirmed.TemporaryFullCharge.Value)
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

        return BatteryMatches(desired, confirmed)
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
            TemporaryFullCharge: false,
            ChargeLimitPercent: int currentLimit,
        } && capabilities.ChargeLimits.Contains(currentLimit);
        if (!admitted || !exactPrecondition)
        {
            return BlockedBatteryPlan("Charge limit");
        }

        bool Matches(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            TemporaryFullCharge: false,
            ChargeLimitPercent: var limit,
        } && limit == value.ChargeLimitPercent;
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
        if (confirmed is { ProtectionEnabled: false, TemporaryFullCharge: false }
            && !enabled)
        {
            return MatchingBatteryPlan("Battery protection", confirmed);
        }

        bool storedLimitAdmitted = capabilities.ChargeLimits.Contains(
            storedPolicy.ChargeLimitPercent);
        if (enabled && !storedLimitAdmitted
            || confirmed?.TemporaryFullCharge is not false)
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
                TemporaryFullCharge: false,
            };
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
            TemporaryFullCharge: false,
            ChargeLimitPercent: var limit,
        } && limit == storedPolicy.ChargeLimitPercent;
        if (MatchesOn(confirmed))
        {
            return MatchingBatteryPlan("Battery protection", confirmed);
        }

        if (confirmed is not
            {
                ProtectionEnabled: false,
                ChargeLimitPercent: null,
                TemporaryFullCharge: false,
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

    internal static bool BatteryMatches(
        DesiredBatteryState desired,
        ConfirmedBatteryState? confirmed) => confirmed is not null
        && desired.ProtectionEnabled == confirmed.ProtectionEnabled
        && (!desired.ProtectionEnabled
            || desired.ConfiguredPolicy.ChargeLimitPercent == confirmed.ChargeLimitPercent)
        && desired.TemporaryFullCharge == confirmed.TemporaryFullCharge;
}
