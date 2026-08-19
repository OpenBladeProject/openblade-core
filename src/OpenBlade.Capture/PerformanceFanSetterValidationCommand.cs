using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class PerformanceFanSetterValidationCommand
{
    public const string ExactTargetConfirmation = "RZ09-0581:1532:02E0:3.01";
    public const string HyperBoostExactTargetConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-1532:0F43:AUTO";
    public const string HyperBoostFromQuietConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-1532:0F43:AUTO:QUIET:RESTORE-QUIET";
    public const string HyperBoostFromPerformanceConfirmation =
        "RZ09-0581:1532:02E0:4.00:PAD-1532:0F43:AUTO:PERFORMANCE:RESTORE-PERFORMANCE";
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private const string LegacyUsage =
        "Usage: OpenBlade.Capture validate-performance-fan " +
        "<balanced|silent|performance|fixed5400|battery-saver|battery-balanced> " +
        "--confirm-target " +
        ExactTargetConfirmation;
    private const string HyperBoostUsage =
        "Usage: OpenBlade.Capture " +
        "validate-rz09-0581-02e0-bios-4.00-hyperboost hyperboost " +
        "--confirm-target " + HyperBoostExactTargetConfirmation;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<PerformanceFanValidationTarget, CancellationToken,
        Task<PerformanceFanSetterValidationResult>> run;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;
    private readonly bool allowHyperBoost;
    private readonly bool allowDirectHyperBoostBaseModes;

    public PerformanceFanSetterValidationCommand(
        Func<PerformanceFanValidationTarget, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null,
        bool allowHyperBoost = false,
        bool allowDirectHyperBoostBaseModes = false)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
        this.allowHyperBoost = allowHyperBoost;
        this.allowDirectHyperBoostBaseModes = allowDirectHyperBoostBaseModes;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out PerformanceFanValidationTarget target))
        {
            await output.WriteLineAsync(
                allowHyperBoost ? HyperBoostUsage : LegacyUsage).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            PerformanceFanSetterValidationResult result =
                await run(target, commandCancellation.Token).ConfigureAwait(false);
            bool hyperBoostTarget = IsHyperBoostTarget(target);
            bool validationSucceeded = result.Success
                && (!hyperBoostTarget
                    || result.BaselineRequiredBaseModeConfirmed
                    && result.FullPowerAdapterConfirmed
                    && result.CoolingPadPresentConfirmed
                    && result.CoolingPadOpaqueZeroConfirmed
                    && result.SafetyFailure == PerformanceFanValidationSafetyFailure.None);
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new CaptureDocument(
                    1,
                    new(
                        TargetBladeDevice.Model,
                        TargetBladeDevice.VendorId.ToString("X4"),
                        TargetBladeDevice.ProductId.ToString("X4"),
                        hyperBoostTarget
                            ? "4.00"
                            : TargetBladeDevice.BiosVersion),
                    ReadOnly: false,
                    ProductionAdmitted: false,
                    hyperBoostTarget
                        ? new HyperBoostValidationBoundary(
                            RequestedBaseMode(target),
                            result.BaselineRequiredBaseModeConfirmed,
                            result.FullPowerAdapterConfirmed,
                            result.CoolingPadPresentConfirmed,
                            result.CoolingPadOpaqueZeroConfirmed,
                            result.SafetyFailure.ToString(),
                            result.BalancedIntermediaryObserved,
                            RetryAllowed: false,
                            ExactRestorationConfirmed: result.Restored,
                            "The opaque-zero observation is only a conservative compatibility gate and does not confirm Auto, active mode, target, or RPM.")
                        : null,
                    result),
                JsonOptions)).ConfigureAwait(false);
            return validationSucceeded
                ? 0
                : result.Failure == PerformanceFanValidationFailure.Canceled
                    ? CanceledExitCode
                    : ValidationFailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private bool TryParse(
        IReadOnlyList<string> arguments,
        out PerformanceFanValidationTarget target)
    {
        target = default;
        if (arguments.Count != 3
            || !arguments[1].Equals("--confirm-target", StringComparison.Ordinal))
        {
            return false;
        }

        bool hyperBoost = arguments[0].StartsWith(
            "hyperboost",
            StringComparison.OrdinalIgnoreCase);
        bool directHyperBoost = arguments[0].Equals(
                "hyperboost-from-quiet",
                StringComparison.OrdinalIgnoreCase)
            || arguments[0].Equals(
                "hyperboost-from-performance",
                StringComparison.OrdinalIgnoreCase);
        if (hyperBoost
            && (!allowHyperBoost || directHyperBoost && !allowDirectHyperBoostBaseModes))
        {
            return false;
        }
        string expectedConfirmation = arguments[0].ToLowerInvariant() switch
        {
            "hyperboost-from-quiet" => HyperBoostFromQuietConfirmation,
            "hyperboost-from-performance" => HyperBoostFromPerformanceConfirmation,
            "hyperboost" => HyperBoostExactTargetConfirmation,
            _ => ExactTargetConfirmation,
        };
        if (!arguments[2].Equals(expectedConfirmation, StringComparison.Ordinal))
        {
            return false;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "balanced" => Assign(PerformanceFanValidationTarget.Balanced, out target),
            "silent" => Assign(PerformanceFanValidationTarget.Silent, out target),
            "performance" => Assign(PerformanceFanValidationTarget.Performance, out target),
            "fixed5400" => Assign(PerformanceFanValidationTarget.Fixed5400, out target),
            "battery-saver" => Assign(
                PerformanceFanValidationTarget.BatterySaver,
                out target),
            "battery-balanced" => Assign(
                PerformanceFanValidationTarget.BatteryBalanced,
                out target),
            "hyperboost" => Assign(
                PerformanceFanValidationTarget.HyperBoost,
                out target),
            "hyperboost-from-quiet" => Assign(
                PerformanceFanValidationTarget.HyperBoostFromSilent,
                out target),
            "hyperboost-from-performance" => Assign(
                PerformanceFanValidationTarget.HyperBoostFromPerformance,
                out target),
            _ => false,
        };
    }

    private static bool Assign(
        PerformanceFanValidationTarget value,
        out PerformanceFanValidationTarget target)
    {
        target = value;
        return true;
    }

    private static bool IsHyperBoostTarget(PerformanceFanValidationTarget target) =>
        target is PerformanceFanValidationTarget.HyperBoost
            or PerformanceFanValidationTarget.HyperBoostFromSilent
            or PerformanceFanValidationTarget.HyperBoostFromPerformance;

    private static string RequestedBaseMode(PerformanceFanValidationTarget target) =>
        target switch
        {
            PerformanceFanValidationTarget.HyperBoostFromSilent => "Quiet",
            PerformanceFanValidationTarget.HyperBoostFromPerformance => "Performance",
            _ => "Balanced",
        };

    private sealed record CaptureDocument(
        int SchemaVersion,
        CaptureDevice Device,
        bool ReadOnly,
        bool ProductionAdmitted,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        HyperBoostValidationBoundary? HyperBoostValidation,
        PerformanceFanSetterValidationResult Result);

    private sealed record HyperBoostValidationBoundary(
        string RequestedBaseMode,
        bool BaselineRequiredBaseModeConfirmed,
        bool FullPowerAdapterConfirmed,
        bool CoolingPadPresentConfirmed,
        bool CoolingPadOpaqueZeroConfirmed,
        string SafetyFailure,
        bool BalancedIntermediaryObserved,
        bool RetryAllowed,
        bool ExactRestorationConfirmed,
        string OpaqueObservationBoundary);

    private sealed record CaptureDevice(
        string Model,
        string VendorId,
        string ProductId,
        string BiosVersion);
}
