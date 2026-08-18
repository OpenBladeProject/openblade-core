using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PerformanceFanSetterValidationCommandTests
{
    [Theory]
    [InlineData("balanced", PerformanceFanValidationTarget.Balanced)]
    [InlineData("silent", PerformanceFanValidationTarget.Silent)]
    [InlineData("performance", PerformanceFanValidationTarget.Performance)]
    [InlineData("fixed5400", PerformanceFanValidationTarget.Fixed5400)]
    [InlineData("battery-saver", PerformanceFanValidationTarget.BatterySaver)]
    [InlineData("battery-balanced", PerformanceFanValidationTarget.BatteryBalanced)]
    public async Task RunAsync_RequiresExactNonInteractiveTargetConfirmation(
        string argument,
        PerformanceFanValidationTarget expected)
    {
        PerformanceFanValidationTarget? invokedTarget = null;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invokedTarget = target;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            [argument, "--confirm-target",
             PerformanceFanSetterValidationCommand.ExactTargetConfirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, invokedTarget);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("readOnly").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.True(root.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal(
            0,
            root.GetProperty("result")
                .GetProperty("candidateFanRpmSamples")
                .GetArrayLength());
        Assert.DoesNotContain("transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("envelope", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_HyperBoostRequiresExactBios400PadAutoConfirmation()
    {
        PerformanceFanValidationTarget? invokedTarget = null;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invokedTarget = target;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance,
            allowHyperBoost: true);

        int exitCode = await command.RunAsync(
            [
                "hyperboost",
                "--confirm-target",
                PerformanceFanSetterValidationCommand.HyperBoostExactTargetConfirmation,
            ],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(PerformanceFanValidationTarget.HyperBoost, invokedTarget);
        Assert.Contains("\"productionAdmitted\": false", output.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "4.00",
            document.RootElement.GetProperty("device").GetProperty("biosVersion").GetString());
    }

    [Theory]
    [InlineData(
        "hyperboost-from-quiet",
        PerformanceFanValidationTarget.HyperBoostFromSilent,
        PerformanceFanSetterValidationCommand.HyperBoostFromQuietConfirmation)]
    [InlineData(
        "hyperboost-from-performance",
        PerformanceFanValidationTarget.HyperBoostFromPerformance,
        PerformanceFanSetterValidationCommand.HyperBoostFromPerformanceConfirmation)]
    public async Task RunAsync_DirectHyperBoostBaseRequiresDedicatedConfirmation(
        string argument,
        PerformanceFanValidationTarget expected,
        string confirmation)
    {
        PerformanceFanValidationTarget? invokedTarget = null;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invokedTarget = target;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance,
            allowHyperBoost: true,
            allowDirectHyperBoostBaseModes: true);

        int exitCode = await command.RunAsync(
            [argument, "--confirm-target", confirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, invokedTarget);
        Assert.Contains("\"productionAdmitted\": false", output.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal(
            "4.00",
            root.GetProperty("device").GetProperty("biosVersion").GetString());
        JsonElement validation = root.GetProperty("hyperBoostValidation");
        Assert.Equal(
            expected == PerformanceFanValidationTarget.HyperBoostFromSilent
                ? "Quiet"
                : "Performance",
            validation.GetProperty("requestedBaseMode").GetString());
        Assert.True(validation.GetProperty("baselineRequiredBaseModeConfirmed").GetBoolean());
        Assert.True(validation.GetProperty("fullPowerAdapterConfirmed").GetBoolean());
        Assert.True(validation.GetProperty("coolingPadPresentConfirmed").GetBoolean());
        Assert.True(validation.GetProperty("coolingPadOpaqueZeroConfirmed").GetBoolean());
        Assert.Equal("None", validation.GetProperty("safetyFailure").GetString());
        Assert.False(validation.GetProperty("balancedIntermediaryObserved").GetBoolean());
        Assert.False(validation.GetProperty("retryAllowed").GetBoolean());
        Assert.True(validation.GetProperty("exactRestorationConfirmed").GetBoolean());
        Assert.Contains(
            "does not confirm Auto, active mode, target, or RPM",
            validation.GetProperty("opaqueObservationBoundary").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HyperBoostBoundaryUsesRunnerIntermediaryEvidence()
    {
        var output = new StringWriter();
        PerformanceFanSetterValidationResult result = Success(
            PerformanceFanValidationTarget.HyperBoostFromSilent) with
        {
            BalancedIntermediaryObserved = true,
        };
        var command = new PerformanceFanSetterValidationCommand(
            (_, _) => Task.FromResult(result),
            output,
            _ => NoopDisposable.Instance,
            allowHyperBoost: true,
            allowDirectHyperBoostBaseModes: true);

        int exitCode = await command.RunAsync(
            [
                "hyperboost-from-quiet",
                "--confirm-target",
                PerformanceFanSetterValidationCommand.HyperBoostFromQuietConfirmation,
            ],
            default);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.True(document.RootElement
            .GetProperty("hyperBoostValidation")
            .GetProperty("balancedIntermediaryObserved")
            .GetBoolean());
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task RunAsync_HyperBoostCannotExitSuccessWithoutEverySafetyGate(
        bool baselineConfirmed,
        bool fullPowerConfirmed,
        bool padPresentConfirmed,
        bool opaqueZeroConfirmed)
    {
        var output = new StringWriter();
        PerformanceFanSetterValidationResult result = Success(
            PerformanceFanValidationTarget.HyperBoostFromSilent) with
        {
            BaselineRequiredBaseModeConfirmed = baselineConfirmed,
            FullPowerAdapterConfirmed = fullPowerConfirmed,
            CoolingPadPresentConfirmed = padPresentConfirmed,
            CoolingPadOpaqueZeroConfirmed = opaqueZeroConfirmed,
        };
        var command = new PerformanceFanSetterValidationCommand(
            (_, _) => Task.FromResult(result),
            output,
            _ => NoopDisposable.Instance,
            allowHyperBoost: true,
            allowDirectHyperBoostBaseModes: true);

        int exitCode = await command.RunAsync(
            [
                "hyperboost-from-quiet",
                "--confirm-target",
                PerformanceFanSetterValidationCommand.HyperBoostFromQuietConfirmation,
            ],
            default);

        Assert.Equal(
            PerformanceFanSetterValidationCommand.ValidationFailureExitCode,
            exitCode);
    }

    [Fact]
    public async Task RunAsync_LegacyCommandCannotInvokeHyperBoost()
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invoked = true;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            [
                "hyperboost",
                "--confirm-target",
                PerformanceFanSetterValidationCommand.HyperBoostExactTargetConfirmation,
            ],
            default);

        Assert.Equal(PerformanceFanSetterValidationCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
    }

    public static TheoryData<string[]> RejectedArguments => new()
    {
        Array.Empty<string>(),
        new[] { "balanced" },
        new[] { "balanced", "--confirm-target", "yes" },
        new[] { "balanced", "--confirm-target", "rz09-0581:1532:02e0:3.01" },
        new[] { "0D/02", "--confirm-target",
            PerformanceFanSetterValidationCommand.ExactTargetConfirmation },
        new[] { "fixed5400", "--confirm-target",
            PerformanceFanSetterValidationCommand.ExactTargetConfirmation, "010136" },
        new[] { "hyperboost", "--confirm-target",
            PerformanceFanSetterValidationCommand.ExactTargetConfirmation },
        new[] { "balanced", "--confirm-target",
            PerformanceFanSetterValidationCommand.HyperBoostExactTargetConfirmation },
    };

    [Theory]
    [MemberData(nameof(RejectedArguments))]
    public async Task RunAsync_RejectsMissingInexactOrRawArguments(string[] arguments)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invoked = true;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(arguments, default);

        Assert.Equal(PerformanceFanSetterValidationCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Contains(
            "--confirm-target RZ09-0581:1532:02E0:3.01",
            output.ToString(),
            StringComparison.Ordinal);
    }

    private static PerformanceFanSetterValidationResult Success(
        PerformanceFanValidationTarget target) => new(
            target,
            new("01010500", "01020500"),
            new("01010000", "01020000"),
            null,
            [],
            new("01010500", "01020500"),
            ApplyAttempted: true,
            ApplyAcknowledged: true,
            CandidateConfirmed: true,
            RestoreAttempted: true,
            RestoreAcknowledged: true,
            Restored: true,
            IndeterminatePartialWrite: false,
            ManualRecoveryRequired: false,
            PerformanceFanValidationFailure.None)
        {
            BaselineRequiredBaseModeConfirmed =
                target is PerformanceFanValidationTarget.HyperBoost
                    or PerformanceFanValidationTarget.HyperBoostFromSilent
                    or PerformanceFanValidationTarget.HyperBoostFromPerformance,
            FullPowerAdapterConfirmed =
                target is PerformanceFanValidationTarget.HyperBoost
                    or PerformanceFanValidationTarget.HyperBoostFromSilent
                    or PerformanceFanValidationTarget.HyperBoostFromPerformance,
            CoolingPadPresentConfirmed =
                target is PerformanceFanValidationTarget.HyperBoost
                    or PerformanceFanValidationTarget.HyperBoostFromSilent
                    or PerformanceFanValidationTarget.HyperBoostFromPerformance,
            CoolingPadOpaqueZeroConfirmed =
                target is PerformanceFanValidationTarget.HyperBoost
                    or PerformanceFanValidationTarget.HyperBoostFromSilent
                    or PerformanceFanValidationTarget.HyperBoostFromPerformance,
        };

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
