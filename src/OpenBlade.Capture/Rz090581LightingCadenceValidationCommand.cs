using System.ComponentModel;
using System.Globalization;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record LightingCadenceMatrixInterfaceEvidence(
    string UsagePage,
    string Usage,
    int FeatureReportLength);

internal sealed record LightingCadenceStageEvidence(
    LightingCadenceStageResult Transport,
    bool VisuallyStable);

internal sealed record Rz090581LightingCadenceEvidence(
    int SchemaVersion,
    string EvidenceKind,
    DeviceIdentity Device,
    LightingCadenceMatrixInterfaceEvidence MatrixInterface,
    string InformationalVersion,
    string CommitHash,
    string ProtocolAssemblySha256,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int BrightnessPercent,
    int MeasurementSeconds,
    IReadOnlyList<int> RequestedRates,
    LightingCadencePattern Pattern,
    bool BaselineBrightnessMatched,
    bool BaselineVisuallyConfirmed,
    IReadOnlyList<LightingCadenceStageEvidence> Stages,
    bool RestorationAttempted,
    bool RestoredBrightnessMatched,
    bool RestoredOwnershipMatched,
    bool RestoredVisuallyConfirmed,
    string? RestoredStateKind,
    string RestorationOutcome,
    bool UnknownPreTestStateRestored,
    bool CleanupRequired,
    bool CleanupCompleted,
    bool CleanupPending,
    bool LightingRecoveryRequired,
    string? LightingRecoveryReason,
    bool TransportQualified,
    bool OpticalValidationRequired,
    string? Failure);

internal sealed class Rz090581LightingCadenceValidationCommand
{
    internal const int UsageExitCode = 1;
    internal const int ValidationFailureExitCode = 2;
    internal const int CanceledExitCode = 4;
    internal const int MinimumMeasurementSeconds = 5;
    internal const int MaximumMeasurementSeconds = 60;
    internal static IReadOnlyList<int> RequiredRates { get; } =
        Array.AsReadOnly(new[] { 30, 45, 60 });

    private const string TargetPrefix = "RZ09-0581:1532:02E0:";
    private const string TargetSuffix = ":000C:0001:375";
    private const string ExactManufacturer = "Razer";
    private const string ExactProductName = "Blade 16 - RZ09-0581";
    private const string ExactSku = "RZ09-05819EN4";
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(2);
    private static readonly RgbColor BaselineGreen = new(0, byte.MaxValue, 0);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<CancellationToken,
        Task<HidSessionResult<IProductionLightingPort>>> openPort;
    private readonly Func<CancellationToken,
        Task<HidSessionResult<LightingBrightnessReading>>> queryBrightness;
    private readonly Func<HostIdentityReadResult> readIdentity;
    private readonly Func<int, TimeSpan, TimeSpan,
        Func<AmbientLightingFrame, CancellationToken, Task>, CancellationToken,
        Task<LightingCadenceStageResult>> runStage;
    private readonly Func<int, TimeSpan, TimeSpan,
        Func<AmbientLightingFrame, CancellationToken, Task>, CancellationToken,
        Task<LightingCadenceStageResult>> runTidalStage;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly ProductionLightingBuildIdentity buildIdentity;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<string, string, CancellationToken, Task> writeArtifact;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<Action, IDisposable> registerCancellation;
    private readonly Func<string?> checkEnvironment;

    internal Rz090581LightingCadenceValidationCommand(
        Func<CancellationToken, Task<HidSessionResult<IProductionLightingPort>>> openPort,
        Func<CancellationToken,
            Task<HidSessionResult<LightingBrightnessReading>>> queryBrightness,
        Func<HostIdentityReadResult> readIdentity,
        Func<int, TimeSpan, TimeSpan,
            Func<AmbientLightingFrame, CancellationToken, Task>, CancellationToken,
            Task<LightingCadenceStageResult>> runStage,
        TextReader input,
        TextWriter output,
        ProductionLightingBuildIdentity buildIdentity,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, string, CancellationToken, Task>? writeArtifact = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Action, IDisposable>? registerCancellation = null,
        Func<string?>? checkEnvironment = null,
        Func<int, TimeSpan, TimeSpan,
            Func<AmbientLightingFrame, CancellationToken, Task>, CancellationToken,
            Task<LightingCadenceStageResult>>? runTidalStage = null)
    {
        this.openPort = openPort ?? throw new ArgumentNullException(nameof(openPort));
        this.queryBrightness = queryBrightness
            ?? throw new ArgumentNullException(nameof(queryBrightness));
        this.readIdentity = readIdentity
            ?? throw new ArgumentNullException(nameof(readIdentity));
        this.runStage = runStage ?? throw new ArgumentNullException(nameof(runStage));
        this.runTidalStage = runTidalStage ?? this.runStage;
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.buildIdentity = buildIdentity
            ?? throw new ArgumentNullException(nameof(buildIdentity));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.writeArtifact = writeArtifact ?? WriteAtomicallyAsync;
        this.delay = delay ?? Task.Delay;
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
        this.checkEnvironment = checkEnvironment ?? CheckWindowsEnvironment;
    }

    internal static Rz090581LightingCadenceValidationCommand CreateWindows(
        TextReader input,
        TextWriter output)
    {
        IProductionLightingPortFactory factory =
            ProductionLightingFactory.CreateWindowsForExactBuildValidation();
        var registry = new WindowsRegistryValueSource();
        var identity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry));
        var runner = new LightingCadenceProbeRunner(TimeProvider.System);
        return new(
            factory.OpenAsync,
            cancellationToken => BatteryValidationFactory
                .CreateWindowsLightingBrightnessQuery()
                .RunAsync(cancellationToken),
            identity.ReadExactIdentity,
            runner.RunAsync,
            input,
            output,
            ProductionLightingEvidenceSource.ReadCurrentBuildIdentity(),
            runTidalStage: (rate, warmup, measurement, pushFrame, cancellationToken) =>
                runner.RunAsync(
                    LightingCadencePattern.Tidal,
                    rate,
                    warmup,
                    measurement,
                    pushFrame,
                    cancellationToken));
    }

    internal static string ExactTargetConfirmation(string biosVersion) =>
        $"{TargetPrefix}{biosVersion}{TargetSuffix}";

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out CadenceOptions options))
        {
            await WriteUsageAsync().ConfigureAwait(false);
            return UsageExitCode;
        }

        HostIdentityReadResult identityResult;
        try
        {
            identityResult = readIdentity();
        }
        catch
        {
            await output.WriteLineAsync(
                "The exact host identity could not be read. No hardware was changed.")
                .ConfigureAwait(false);
            return ValidationFailureExitCode;
        }
        HostIdentityEvidence? host = identityResult.Evidence;
        if (host is null
            || identityResult.Failure is not null
            || !IsExactHost(host.Value)
            || !TargetBladeDevice.IsSupportedBiosVersion(host.Value.BiosVersion)
            || !options.Confirmation.Equals(
                ExactTargetConfirmation(host.Value.BiosVersion),
                StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "The confirmation does not match this exact admitted RZ09-0581 host. " +
                "No hardware was changed.").ConfigureAwait(false);
            return ValidationFailureExitCode;
        }

        if (checkEnvironment() is { } environmentFailure)
        {
            await output.WriteLineAsync(environmentFailure).ConfigureAwait(false);
            return ValidationFailureExitCode;
        }
        if (!ValidBuildIdentity(buildIdentity))
        {
            await output.WriteLineAsync(
                "The exact-build identity is incomplete. No hardware was changed.")
                .ConfigureAwait(false);
            return ValidationFailureExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        DateTimeOffset startedAtUtc = utcNow();
        IProductionLightingPort? port = null;
        var stages = new List<LightingCadenceStageEvidence>();
        bool hardwareTouched = false;
        bool baselineBrightnessMatched = false;
        bool baselineVisuallyConfirmed = false;
        bool restorationAttempted = false;
        bool restoredBrightnessMatched = false;
        bool restoredOwnershipMatched = false;
        bool restoredVisuallyConfirmed = false;
        bool cleanupRequired = false;
        bool cleanupCompleted = false;
        bool cleanupPending = false;
        bool lightingRecoveryRequired = false;
        string? lightingRecoveryReason = null;
        bool canceled = false;
        string? failure = null;
        LightingSettings baseline = LightingSettingsFactory.Static(
            BaselineGreen,
            options.Brightness);

        try
        {
            await output.WriteLineAsync(
                "Transport timing is measured here. Operator confirmation validates " +
                "visible stability; exact optical refresh measurement is outside this " +
                "validator.").ConfigureAwait(false);
            await output.WriteLineAsync($"Pattern: {PatternName(options.Pattern)}.")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                "This replaces the current keyboard effect. OpenBlade cannot read or " +
                "restore an unknown pre-test effect. A healthy or known-not-applied " +
                $"outcome will finish at a known Static green {options.Brightness}% baseline.")
                .ConfigureAwait(false);
            bool overwriteAuthorized = await PromptYesAsync(
                "Proceed with replacing the current effect?",
                commandCancellation.Token).ConfigureAwait(false);
            if (!overwriteAuthorized)
            {
                failure = "effectReplacementDeclined";
            }
            else
            {
                HidSessionResult<IProductionLightingPort> opened =
                    await openPort(commandCancellation.Token).ConfigureAwait(false);
                port = opened.Value;
                if (port is null || opened.Failure is not null)
                {
                    failure = $"controlsUnavailable:{opened.Failure?.ToString() ?? "unknown"}";
                }
                else if (!port.Capabilities.LightingEffects.Contains(LightingEffect.Static)
                    || !port.Capabilities.LightingEffects.Contains(
                        LightingEffect.AmbientAwareness))
                {
                    failure = "exactEffectsNotAdmitted";
                }
                else
                {
                    await port.EnsureDriverModeAsync(commandCancellation.Token)
                        .ConfigureAwait(false);
                    hardwareTouched = true;
                    await port.SetLightingAsync(baseline, commandCancellation.Token)
                        .ConfigureAwait(false);
                    baselineBrightnessMatched = await BrightnessMatchesAsync(
                        options.Brightness,
                        commandCancellation.Token).ConfigureAwait(false);
                    baselineVisuallyConfirmed = baselineBrightnessMatched
                        && await PromptYesAsync(
                            $"Is the entire keyboard Static green at {options.Brightness}%?",
                            commandCancellation.Token).ConfigureAwait(false);
                    if (!baselineBrightnessMatched)
                    {
                        failure = "baselineBrightnessReadbackMismatch";
                    }
                    else if (!baselineVisuallyConfirmed)
                    {
                        failure = "baselineVisualConfirmationRejected";
                    }

                    foreach (int rate in options.Rates)
                    {
                        if (failure is not null)
                        {
                            break;
                        }

                        await output.WriteLineAsync(
                            $"Running {rate} Hz: 2 s warmup, {options.Seconds} s measured...")
                            .ConfigureAwait(false);
                        await port.SetLightingAsync(
                            LightingSettingsFactory.AmbientAwareness(
                                AmbientAwarenessRegion.Full,
                                options.Brightness),
                            commandCancellation.Token).ConfigureAwait(false);
                        Func<int, TimeSpan, TimeSpan,
                            Func<AmbientLightingFrame, CancellationToken, Task>,
                            CancellationToken, Task<LightingCadenceStageResult>> stageRunner =
                            options.Pattern == LightingCadencePattern.Tidal
                                ? runTidalStage
                                : runStage;
                        LightingCadenceStageResult transport = await stageRunner(
                            rate,
                            WarmupDuration,
                            TimeSpan.FromSeconds(options.Seconds),
                            port.PushAmbientLightingFrameAsync,
                            commandCancellation.Token).ConfigureAwait(false);
                        bool visuallyStable = transport.Failure is null
                            && await PromptYesAsync(
                                $"Did the {rate} Hz {PatternName(options.Pattern)} effect " +
                                "remain stable, without freezing or corruption?",
                                commandCancellation.Token).ConfigureAwait(false);
                        stages.Add(new(transport, visuallyStable));

                        if (transport.FailureSubmissionState ==
                            WriteSubmissionState.Indeterminate)
                        {
                            lightingRecoveryRequired = true;
                            lightingRecoveryReason = "indeterminateMatrixWrite";
                            failure = "indeterminateMatrixWrite";
                            break;
                        }
                        if (transport.Failure is not null)
                        {
                            failure = transport.Failure;
                            break;
                        }
                        if (!transport.TransportQualified)
                        {
                            failure = $"transportQualificationFailed:{rate}";
                            break;
                        }
                        if (!visuallyStable)
                        {
                            failure = $"visualStabilityRejected:{rate}";
                            break;
                        }

                        await port.SetLightingAsync(baseline, commandCancellation.Token)
                            .ConfigureAwait(false);
                        await delay(CooldownDuration, commandCancellation.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (BladeWriteException exception)
        {
            lightingRecoveryRequired =
                exception.SubmissionState == WriteSubmissionState.Indeterminate;
            lightingRecoveryReason = lightingRecoveryRequired
                ? "indeterminateMatrixWrite"
                : null;
            failure = $"matrixWrite:{exception.SubmissionState}";
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            canceled = true;
            failure = "canceled";
        }
        catch (Exception exception)
        {
            failure = $"validationFailed:{ExceptionKind(exception)}";
        }
        finally
        {
            if (port is not null && hardwareTouched && !lightingRecoveryRequired)
            {
                restorationAttempted = true;
                bool restorationWriteCompleted = false;
                try
                {
                    await port.SetLightingAsync(baseline, CancellationToken.None)
                        .ConfigureAwait(false);
                    restorationWriteCompleted = true;
                    restoredBrightnessMatched = await BrightnessMatchesAsync(
                        options.Brightness,
                        CancellationToken.None).ConfigureAwait(false);
                    restoredOwnershipMatched =
                        port.OwnershipState == LightingRendererOwnershipState.Static;
                    restoredVisuallyConfirmed = !canceled
                        && await PromptYesAsync(
                            $"Was Static green at {options.Brightness}% restored across the keyboard?",
                            CancellationToken.None).ConfigureAwait(false);
                }
                catch (BladeWriteException exception)
                {
                    lightingRecoveryRequired = true;
                    lightingRecoveryReason = exception.SubmissionState ==
                        WriteSubmissionState.Indeterminate
                        ? "indeterminateRestoreWrite"
                        : "baselineRestoreKnownNotApplied";
                    failure = AppendFailure(
                        failure,
                        $"restoreWrite:{exception.SubmissionState}");
                }
                catch (Exception exception)
                {
                    if (!restorationWriteCompleted)
                    {
                        lightingRecoveryRequired = true;
                        lightingRecoveryReason = "baselineRestoreFailed";
                    }
                    failure = AppendFailure(
                        failure,
                        $"restoreFailed:{ExceptionKind(exception)}");
                }
            }

            if (port is not null)
            {
                cleanupRequired = true;
                try
                {
                    cleanupCompleted = await port.DisposeWithinCleanupDeadlineAsync()
                        .ConfigureAwait(false);
                    if (!cleanupCompleted)
                    {
                        cleanupPending = true;
                        failure = AppendFailure(failure, "cleanupPending");
                    }
                }
                catch
                {
                    cleanupPending = true;
                    failure = AppendFailure(failure, "disposeFailed");
                }
            }
        }

        bool restored = restorationAttempted
            && restoredBrightnessMatched
            && restoredOwnershipMatched
            && restoredVisuallyConfirmed;
        string restorationOutcome = restored
            ? "knownStaticGreenBaselineConfirmed"
            : !restorationAttempted && lightingRecoveryRequired
                ? "skippedForUnprovenLightingState"
                : restorationAttempted
                    ? "knownStaticGreenBaselineUnconfirmed"
                    : "notRequired";
        bool transportQualified = stages.Count == options.Rates.Count
            && stages.All(stage => stage.Transport.TransportQualified);
        bool passed = failure is null
            && baselineBrightnessMatched
            && baselineVisuallyConfirmed
            && transportQualified
            && stages.All(stage => stage.VisuallyStable)
            && restored;
        failure ??= passed ? null : "hardwareGatesIncomplete";
        DateTimeOffset completedAtUtc = utcNow();
        var evidence = new Rz090581LightingCadenceEvidence(
            SchemaVersion: 3,
            EvidenceKind: "rz090581LightingCadenceValidation",
            new DeviceIdentity(
                TargetBladeDevice.Model,
                TargetBladeDevice.VendorId,
                TargetBladeDevice.ProductId,
                host.Value.BiosVersion),
            new("000C", "0001", TargetBladeDevice.MatrixFeatureReportByteLength),
            buildIdentity.InformationalVersion,
            buildIdentity.CommitHash,
            buildIdentity.ProtocolAssemblySha256,
            startedAtUtc,
            completedAtUtc,
            options.Brightness,
            options.Seconds,
            options.Rates,
            options.Pattern,
            baselineBrightnessMatched,
            baselineVisuallyConfirmed,
            stages,
            restorationAttempted,
            restoredBrightnessMatched,
            restoredOwnershipMatched,
            restoredVisuallyConfirmed,
            RestoredStateKind: restored ? "knownStaticGreenBaseline" : null,
            restorationOutcome,
            UnknownPreTestStateRestored: false,
            cleanupRequired,
            cleanupCompleted,
            cleanupPending,
            lightingRecoveryRequired,
            lightingRecoveryReason,
            transportQualified,
            OpticalValidationRequired: true,
            failure);

        string stem =
            $"rz09-0581-lighting-cadence-{completedAtUtc:yyyyMMddTHHmmssfffZ}";
        string jsonPath = Path.Combine(options.OutputDirectory, $"{stem}.json");
        string markdownPath = Path.Combine(options.OutputDirectory, $"{stem}.md");
        bool artifactsWritten = true;
        try
        {
            await writeArtifact(
                jsonPath,
                JsonSerializer.Serialize(evidence, JsonOptions),
                CancellationToken.None).ConfigureAwait(false);
            await writeArtifact(
                markdownPath,
                CreateMarkdown(evidence),
                CancellationToken.None).ConfigureAwait(false);
            await output.WriteLineAsync($"Validation result: {jsonPath}")
                .ConfigureAwait(false);
            await output.WriteLineAsync($"Summary: {markdownPath}")
                .ConfigureAwait(false);
        }
        catch
        {
            artifactsWritten = false;
            await output.WriteLineAsync("Validation artifacts could not be written.")
                .ConfigureAwait(false);
        }

        if (lightingRecoveryRequired)
        {
            await output.WriteLineAsync(
                $"Lighting recovery is required ({lightingRecoveryReason ?? "unknown"}). " +
                "The known Static green baseline was not confirmed; do not retry the " +
                "validator until the keyboard state is recovered manually.")
                .ConfigureAwait(false);
        }
        else if (restorationAttempted && !restored)
        {
            await output.WriteLineAsync(
                "The known Static green baseline restoration was attempted but not " +
                "fully confirmed. Inspect the keyboard state before another validation.")
                .ConfigureAwait(false);
        }
        if (cleanupPending)
        {
            await output.WriteLineAsync(
                "Lighting-port cleanup did not complete within its bounded deadline. " +
                "Cleanup status is separate from the lighting restoration evidence; " +
                "do not start another validation while cleanup may still be draining.")
                .ConfigureAwait(false);
        }
        if (!artifactsWritten)
        {
            return ValidationFailureExitCode;
        }

        return passed
            ? 0
            : canceled
                ? CanceledExitCode
                : ValidationFailureExitCode;
    }

    private async Task<bool> BrightnessMatchesAsync(
        byte brightness,
        CancellationToken cancellationToken)
    {
        HidSessionResult<LightingBrightnessReading> result =
            await queryBrightness(cancellationToken).ConfigureAwait(false);
        return result.Failure is null
            && result.Value?.RawValue == LightingBrightnessProtocol.PercentToRaw(brightness);
    }

    private async Task<bool> PromptYesAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"{prompt} Type YES or NO:").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(response?.Trim(), "YES", StringComparison.Ordinal);
    }

    private async Task WriteUsageAsync()
    {
        await output.WriteLineAsync(
            "Usage: OpenBlade.Capture validate-rz09-0581-lighting-cadence " +
            "--confirm-target RZ09-0581:1532:02E0:<BIOS>:000C:0001:375 " +
            "--rates 30,45,60 --pattern <marker|tidal> " +
            "--brightness <1-100> --seconds <5-60> " +
            "--output <directory>").ConfigureAwait(false);
    }

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        out CadenceOptions options)
    {
        options = null!;
        if (arguments.Count != 12
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[2].Equals("--rates", StringComparison.Ordinal)
            || !arguments[4].Equals("--pattern", StringComparison.Ordinal)
            || !arguments[6].Equals("--brightness", StringComparison.Ordinal)
            || !arguments[8].Equals("--seconds", StringComparison.Ordinal)
            || !arguments[10].Equals("--output", StringComparison.Ordinal)
            || !arguments[1].StartsWith(TargetPrefix, StringComparison.Ordinal)
            || !arguments[1].EndsWith(TargetSuffix, StringComparison.Ordinal)
            || !TryParseRates(arguments[3], out int[] rates)
            || !TryParsePattern(arguments[5], out LightingCadencePattern pattern)
            || !byte.TryParse(
                arguments[7],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out byte brightness)
            || brightness is 0 or > 100
            || !int.TryParse(
                arguments[9],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int seconds)
            || seconds is < MinimumMeasurementSeconds or > MaximumMeasurementSeconds
            || string.IsNullOrWhiteSpace(arguments[11]))
        {
            return false;
        }

        try
        {
            options = new(
                arguments[1],
                rates,
                pattern,
                brightness,
                seconds,
                Path.GetFullPath(arguments[11]));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryParseRates(string value, out int[] rates)
    {
        rates = [];
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != RequiredRates.Count)
        {
            return false;
        }

        rates = new int[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out rates[index])
                || rates[index] != RequiredRates[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePattern(
        string value,
        out LightingCadencePattern pattern)
    {
        pattern = value switch
        {
            "marker" => LightingCadencePattern.Marker,
            "tidal" => LightingCadencePattern.Tidal,
            _ => default,
        };
        return value is "marker" or "tidal";
    }

    private static string PatternName(LightingCadencePattern pattern) => pattern switch
    {
        LightingCadencePattern.Marker => "marker",
        LightingCadencePattern.Tidal => "Tidal",
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };

    private static string CreateMarkdown(Rz090581LightingCadenceEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# RZ09-0581 lighting cadence validation");
        builder.AppendLine();
        builder.AppendLine($"- Completed: {evidence.CompletedAtUtc:O}");
        builder.AppendLine($"- Result: {(evidence.Failure is null ? "passed" : "failed")}");
        builder.AppendLine($"- Failure: {evidence.Failure ?? "none"}");
        builder.AppendLine($"- Pattern: {PatternName(evidence.Pattern)}");
        builder.AppendLine($"- Restoration outcome: {evidence.RestorationOutcome}");
        builder.AppendLine($"- Restored state: {evidence.RestoredStateKind ?? "none"}");
        builder.AppendLine(
            $"- Unknown pre-test state restored: {evidence.UnknownPreTestStateRestored}");
        builder.AppendLine($"- Cleanup required: {evidence.CleanupRequired}");
        builder.AppendLine($"- Cleanup completed: {evidence.CleanupCompleted}");
        builder.AppendLine($"- Cleanup pending: {evidence.CleanupPending}");
        builder.AppendLine(
            $"- Lighting recovery required: {evidence.LightingRecoveryRequired}");
        builder.AppendLine(
            $"- Lighting recovery reason: {evidence.LightingRecoveryReason ?? "none"}");
        builder.AppendLine("- Optical validation: required; transport completion is not LED refresh proof");
        builder.AppendLine("- CPU: validator process only; 100% LP equals one fully occupied logical processor; machine capacity divides by the recorded logical-processor count");
        builder.AppendLine();
        builder.AppendLine("| Target | Completed | Skipped | Achieved | p99 write | Max gap | CPU (1 LP=100%) | CPU (machine capacity) | Transport | Visual |");
        builder.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :---: | :---: |");
        foreach (LightingCadenceStageEvidence stage in evidence.Stages)
        {
            LightingCadenceStageResult value = stage.Transport;
            builder.AppendLine(
                $"| {value.TargetFramesPerSecond} Hz | {value.CompletedFrames}/{value.ScheduledFrames} | " +
                $"{value.SkippedFrames} | {value.AchievedFramesPerSecond:F2} Hz | " +
                $"{Format(value.WriteDuration.P99Milliseconds)} | " +
                $"{value.MaximumCompletionGapMilliseconds:F2} ms | " +
                $"{FormatPercent(value.ProcessorUsage?.SingleLogicalProcessorPercent)} | " +
                $"{FormatPercent(value.ProcessorUsage?.WholeMachineCapacityPercent)} | " +
                $"{(value.TransportQualified ? "pass" : "fail")} | " +
                $"{(stage.VisuallyStable ? "pass" : "fail")} |");
        }
        return builder.ToString();
    }

    private static string Format(double? milliseconds) =>
        milliseconds is null ? "n/a" : $"{milliseconds.Value:F2} ms";

    private static string FormatPercent(double? percent) =>
        percent is null ? "n/a" : $"{percent.Value:F2}%";

    private static bool ValidBuildIdentity(ProductionLightingBuildIdentity identity) =>
        identity.CommitHash.Length == 40
        && identity.CommitHash.All(Uri.IsHexDigit)
        && identity.InformationalVersion.EndsWith(
            $"+{identity.CommitHash}",
            StringComparison.OrdinalIgnoreCase)
        && identity.ProtocolAssemblySha256.Length == 64
        && identity.ProtocolAssemblySha256.All(Uri.IsHexDigit);

    private static bool IsExactHost(HostIdentityEvidence host) =>
        host.Manufacturer.Equals(ExactManufacturer, StringComparison.Ordinal)
        && host.ProductName.Equals(ExactProductName, StringComparison.Ordinal)
        && host.Sku.Equals(ExactSku, StringComparison.Ordinal);

    private static string? CheckWindowsEnvironment()
    {
        try
        {
            using var service = new ServiceController("OpenBlade");
            return service.Status == ServiceControllerStatus.Stopped
                ? null
                : "Stop the OpenBlade service before validating lighting. No hardware was changed.";
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1060)
        {
            return null;
        }
        catch
        {
            return "OpenBlade could not verify that its service is stopped. No hardware was changed.";
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("The validation artifact requires a parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static string ExceptionKind(Exception exception) => exception switch
    {
        System.TimeoutException => "timeout",
        UnauthorizedAccessException => "accessDenied",
        IOException => "io",
        ObjectDisposedException => "disposed",
        InvalidOperationException => "invalidOperation",
        _ => "unexpected",
    };

    private static string AppendFailure(string? current, string additional) =>
        current is null ? additional : $"{current}|{additional}";

    private sealed record CadenceOptions(
        string Confirmation,
        IReadOnlyList<int> Rates,
        LightingCadencePattern Pattern,
        byte Brightness,
        int Seconds,
        string OutputDirectory);
}
