using System.ComponentModel;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class LightingValidationCommand
{
    public const string ExactTargetConfirmation = "RZ09-0581:1532:02E0:3.01";
    public const int UsageExitCode = 1;
    public const int ValidationFailureExitCode = 2;
    public const int CanceledExitCode = 4;

    private const byte ReactiveValidationMatrixSlot = 91; // Captured Space slot.
    private const string Usage =
        "Usage: OpenBlade.Capture validate-lighting --confirm-target " +
        ExactTargetConfirmation + " --brightness <1-100>";

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
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly ProductionLightingBuildIdentity buildIdentity;
    private readonly string successArtifactPath;
    private readonly Func<string, string, CancellationToken, Task> writeArtifact;
    private readonly Func<Action, IDisposable> registerCancellation;
    private readonly Func<string?> checkEnvironment;

    public LightingValidationCommand(
        Func<CancellationToken, Task<HidSessionResult<IProductionLightingPort>>> openPort,
        Func<CancellationToken, Task<HidSessionResult<LightingBrightnessReading>>>
            queryBrightness,
        TextReader input,
        TextWriter output,
        string informationalVersion,
        string commitHash,
        string protocolAssemblySha256,
        string successArtifactPath,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, string, CancellationToken, Task>? writeArtifact = null,
        Func<Action, IDisposable>? registerCancellation = null,
        Func<string?>? checkEnvironment = null)
    {
        this.openPort = openPort ?? throw new ArgumentNullException(nameof(openPort));
        this.queryBrightness = queryBrightness
            ?? throw new ArgumentNullException(nameof(queryBrightness));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        buildIdentity = new(
            informationalVersion ?? throw new ArgumentNullException(nameof(informationalVersion)),
            commitHash ?? throw new ArgumentNullException(nameof(commitHash)),
            protocolAssemblySha256
                ?? throw new ArgumentNullException(nameof(protocolAssemblySha256)));
        ArgumentException.ThrowIfNullOrWhiteSpace(successArtifactPath);
        this.successArtifactPath = successArtifactPath;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.writeArtifact = writeArtifact ?? WriteAtomicallyAsync;
        this.registerCancellation = registerCancellation ?? RegisterConsoleCancellation;
        this.checkEnvironment = checkEnvironment ?? (() => null);
    }

    internal static LightingValidationCommand CreateWindows(
        TextReader input,
        TextWriter output)
    {
        IProductionLightingPortFactory factory =
            ProductionLightingFactory.CreateWindowsForExactBuildValidation();
        ProductionLightingBuildIdentity identity =
            ProductionLightingEvidenceSource.ReadCurrentBuildIdentity();
        return new LightingValidationCommand(
            factory.OpenAsync,
            cancellationToken => BatteryValidationFactory
                .CreateWindowsLightingBrightnessQuery()
                .RunAsync(cancellationToken),
            input,
            output,
            identity.InformationalVersion,
            identity.CommitHash,
            identity.ProtocolAssemblySha256,
            ProductionLightingEvidenceSource.GetDefaultOpenBladeValidationPath(),
            checkEnvironment: CheckWindowsEnvironment);
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out byte brightness))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => TryCancel(commandCancellation));
        DateTimeOffset startedAtUtc = utcNow();
        IProductionLightingPort? port = null;
        bool hardwareTouched = false;
        bool canceled = false;
        string? failure = null;
        var actions = new List<ProductionLightingValidationActionEvidence>();
        LightingSettings baselineSettings =
            LightingSettingsFactory.Static(BaselineGreen, brightness);
        int baselineExpectedRaw = LightingBrightnessProtocol.PercentToRaw(brightness);
        int? baselineObservedRaw = null;
        bool baselineBrightnessMatched = false;
        bool baselineVisuallyConfirmed = false;
        bool baselineRestoreAttempted = false;
        int? baselineRestoreObservedRaw = null;
        bool baselineRestoreBrightnessMatched = false;
        bool baselineRestoreVisuallyConfirmed = false;
        bool baselineRestored = false;
        string validationStage = "preflight";

        try
        {
            if (!ValidBuildIdentity(buildIdentity))
            {
                failure = "buildIdentityUnavailable";
            }
            else if (checkEnvironment() is { } environmentFailure)
            {
                failure = "unsafeValidationEnvironment";
                await output.WriteLineAsync(environmentFailure).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(
                    "This replaces the current keyboard effect. OpenBlade cannot read or " +
                    "restore an unknown pre-test effect. It will finish at a known Static " +
                    $"green baseline at {brightness}%.").ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"Type {ExactTargetConfirmation} to authorize this exact laptop:")
                    .ConfigureAwait(false);
                string? confirmation = await input
                    .ReadLineAsync(commandCancellation.Token)
                    .ConfigureAwait(false);
                if (!string.Equals(
                    confirmation,
                    ExactTargetConfirmation,
                    StringComparison.Ordinal))
                {
                    failure = "targetConfirmationDeclined";
                }
                else
                {
                    validationStage = "openPort";
                    HidSessionResult<IProductionLightingPort> opened =
                        await openPort(commandCancellation.Token).ConfigureAwait(false);
                    port = opened.Value;
                    if (port is null || opened.Failure is not null)
                    {
                        failure = "controlsUnavailable";
                    }
                    else if (!Enum.GetValues<LightingEffect>().All(
                        port.Capabilities.LightingEffects.Contains))
                    {
                        failure = "typedEffectsNotAdmitted";
                    }
                    else
                    {
                        hardwareTouched = true;
                        validationStage = "knownBaselineApply";
                        await port.SetLightingAsync(
                            baselineSettings,
                            commandCancellation.Token).ConfigureAwait(false);
                        validationStage = "knownBaselineBrightnessReadback";
                        (baselineObservedRaw, baselineBrightnessMatched) =
                            await QueryBrightnessAsync(
                                baselineExpectedRaw,
                                commandCancellation.Token).ConfigureAwait(false);
                        validationStage = "knownBaselineVisualConfirmation";
                        bool baselineVisualAccepted = baselineBrightnessMatched
                            && await PromptYesAsync(
                                $"Is the entire keyboard Static green at {brightness}%?",
                                commandCancellation.Token).ConfigureAwait(false);
                        baselineVisuallyConfirmed = baselineVisualAccepted
                            && OwnershipMatches(port, baselineSettings);
                        if (!baselineVisuallyConfirmed)
                        {
                            failure = !baselineBrightnessMatched
                                ? "knownBaselineBrightnessMismatch"
                                : !baselineVisualAccepted
                                    ? "knownBaselineVisualRejected"
                                    : "rendererOwnershipLost";
                        }
                        else
                        {
                            foreach (ValidationActionSpec spec in CreateActionSpecs(brightness))
                            {
                                ProductionLightingValidationActionEvidence action =
                                    await RunActionAsync(
                                        port,
                                        spec,
                                        commandCancellation.Token).ConfigureAwait(false);
                                actions.Add(action);
                                if (!ActionPassed(action))
                                {
                                    failure = action.Failure ?? "actionFailed";
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            canceled = true;
            failure = "canceled";
        }
        catch (Exception exception)
        {
            failure = CreateFailureCode(validationStage, exception);
            await WriteFailureAsync(failure).ConfigureAwait(false);
        }
        finally
        {
            if (port is not null && hardwareTouched)
            {
                baselineRestoreAttempted = true;
                try
                {
                    await port.SetLightingAsync(baselineSettings, CancellationToken.None)
                        .ConfigureAwait(false);
                    (baselineRestoreObservedRaw, baselineRestoreBrightnessMatched) =
                        await QueryBrightnessAsync(
                            baselineExpectedRaw,
                            CancellationToken.None).ConfigureAwait(false);
                    baselineRestoreVisuallyConfirmed = !canceled
                        && await PromptYesAsync(
                            $"Was the known Static green {brightness}% baseline restored?",
                            CancellationToken.None).ConfigureAwait(false)
                        && OwnershipMatches(port, baselineSettings);
                    baselineRestored = baselineRestoreObservedRaw == baselineExpectedRaw
                        && baselineRestoreBrightnessMatched
                        && baselineRestoreVisuallyConfirmed;
                }
                catch (Exception exception)
                {
                    baselineRestored = false;
                    string restoreFailure = CreateFailureCode(
                        "knownBaselineRestore",
                        exception);
                    failure = failure is null
                        ? restoreFailure
                        : $"{failure}|{restoreFailure}";
                    await WriteFailureAsync(restoreFailure).ConfigureAwait(false);
                }
            }

            if (port is not null)
            {
                try
                {
                    await port.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    baselineRestored = false;
                }
            }
        }

        bool allActionsApplied = actions.Count == 9
            && actions.All(action => action.ApplySucceeded)
            && actions.Where(action => action.ReactivePulseRequired)
                .All(action => action.ReactivePulseApplied);
        bool brightnessReadBack = baselineBrightnessMatched
            && actions.Count == 9
            && actions.All(action => action.BrightnessMatched)
            && baselineRestored;
        bool transitionsAcknowledged = actions.Count == 9
            && actions.All(action => action.ApplySucceeded);
        bool visuallyConfirmed = baselineVisuallyConfirmed
            && actions.Count == 9
            && actions.All(action => action.VisuallyConfirmed)
            && baselineRestored;
        bool allGates = allActionsApplied
            && brightnessReadBack
            && transitionsAcknowledged
            && visuallyConfirmed
            && baselineRestoreAttempted
            && baselineRestored
            && failure is null;
        failure ??= allGates ? null : "hardwareGatesIncomplete";

        var evidence = new ProductionLightingOpenBladeValidationEvidence(
            SchemaVersion: 2,
            EvidenceKind: "openBladeExactBuildHardwareValidation",
            Device: new(
                TargetBladeDevice.Model,
                TargetBladeDevice.VendorId,
                TargetBladeDevice.ProductId,
                TargetBladeDevice.BiosVersion),
            buildIdentity.InformationalVersion,
            buildIdentity.CommitHash,
            buildIdentity.ProtocolAssemblySha256,
            startedAtUtc,
            ValidatedAtUtc: utcNow(),
            new(
                baselineSettings,
                baselineExpectedRaw,
                baselineObservedRaw,
                baselineBrightnessMatched,
                baselineVisuallyConfirmed),
            actions.ToArray(),
            AllSupportedEffectsApplied: allActionsApplied,
            BrightnessIndependentReadback: brightnessReadBack,
            TransitionAcknowledged: transitionsAcknowledged,
            MatrixWriteVisuallyConfirmed: visuallyConfirmed,
            MatrixReadbackAvailable: false,
            BaselineRestoreAttempted: baselineRestoreAttempted,
            BaselineRestoreObservedBrightnessRaw: baselineRestoreObservedRaw,
            BaselineRestoreBrightnessMatched: baselineRestoreBrightnessMatched,
            BaselineRestoreVisuallyConfirmed: baselineRestoreVisuallyConfirmed,
            BaselineRestored: baselineRestored,
            RestoredStateKind: "knownStaticGreenBaseline",
            UnknownPreTestStateRestored: false,
            Failure: failure);

        string json = JsonSerializer.Serialize(evidence, JsonOptions);
        string artifactPath = allGates
            ? successArtifactPath
            : CreateFailedArtifactPath(successArtifactPath, evidence.ValidatedAtUtc);
        try
        {
            await writeArtifact(artifactPath, json, CancellationToken.None)
                .ConfigureAwait(false);
            await output.WriteLineAsync($"Validation result: {artifactPath}")
                .ConfigureAwait(false);
        }
        catch
        {
            await output.WriteLineAsync("Validation result could not be written.")
                .ConfigureAwait(false);
            return ValidationFailureExitCode;
        }

        return allGates
            ? 0
            : canceled
                ? CanceledExitCode
                : ValidationFailureExitCode;
    }

    private async Task<ProductionLightingValidationActionEvidence> RunActionAsync(
        IProductionLightingPort port,
        ValidationActionSpec spec,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = utcNow();
        int expectedRaw = LightingBrightnessProtocol.PercentToRaw(
            spec.Settings.Brightness);
        int? observedRaw = null;
        bool applySucceeded = false;
        bool brightnessMatched = false;
        bool pulseApplied = false;
        bool visualConfirmed = false;
        string? failure = null;
        try
        {
            await output.WriteLineAsync($"Applying {spec.Description}...")
                .ConfigureAwait(false);
            await port.SetLightingAsync(spec.Settings, cancellationToken)
                .ConfigureAwait(false);
            applySucceeded = true;
            (observedRaw, brightnessMatched) = await QueryBrightnessAsync(
                expectedRaw,
                cancellationToken).ConfigureAwait(false);
            if (!brightnessMatched)
            {
                failure = "brightnessReadbackMismatch";
            }
            else
            {
                if (spec.ReactivePulseRequired)
                {
                    await output.WriteLineAsync(
                        "Press Enter, then watch the Space key for the reactive pulse.")
                        .ConfigureAwait(false);
                    _ = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    await port.PulseReactiveKeyAsync(
                        new ReactiveKeyPulse(ReactiveValidationMatrixSlot),
                        cancellationToken).ConfigureAwait(false);
                    pulseApplied = true;
                }

                visualConfirmed = await PromptYesAsync(
                    $"Did {spec.Description} look correct?",
                    cancellationToken).ConfigureAwait(false);
                if (visualConfirmed && !OwnershipMatches(port, spec.Settings))
                {
                    visualConfirmed = false;
                    failure = "rendererOwnershipLost";
                }
                else if (!visualConfirmed)
                {
                    failure = "visualConfirmationRejected";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            failure = "typedApplyFailed";
        }

        return new(
            spec.Name,
            spec.Settings,
            startedAtUtc,
            CompletedAtUtc: utcNow(),
            applySucceeded,
            expectedRaw,
            observedRaw,
            brightnessMatched,
            spec.ReactivePulseRequired,
            pulseApplied,
            visualConfirmed,
            failure);
    }

    private async Task<(int? ObservedRaw, bool Matched)> QueryBrightnessAsync(
        int expectedRaw,
        CancellationToken cancellationToken)
    {
        HidSessionResult<LightingBrightnessReading> result =
            await queryBrightness(cancellationToken).ConfigureAwait(false);
        int? observed = result.Failure is null ? result.Value?.RawValue : null;
        return (observed, observed == expectedRaw);
    }

    private async Task<bool> PromptYesAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"{prompt} Type yes or no:").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteFailureAsync(string failureCode)
    {
        await output.WriteLineAsync(
            $"Validation diagnostic: {failureCode}").ConfigureAwait(false);
    }

    private static string CreateFailureCode(string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is BladeWriteException write)
        {
            string operation = WriteOperationCode(write);
            string inner = write.InnerException is null
                ? string.Empty
                : $".{ExceptionKind(write.InnerException)}";
            return $"validationFailed:{stage}:bladeWrite." +
                $"{write.SubmissionState}.{operation}{inner}";
        }

        return $"validationFailed:{stage}:{ExceptionKind(exception)}";
    }

    private static string WriteOperationCode(BladeWriteException exception)
    {
        const string commandPrefix = "The lighting command failed (";
        const string matrixPrefix = "The matrix write failed (";
        if (TryReadParenthesizedCode(exception.Message, commandPrefix, out string command))
        {
            return $"lightingCommand.{command}";
        }

        if (TryReadParenthesizedCode(exception.Message, matrixPrefix, out string matrix))
        {
            return $"matrixWrite.{matrix}";
        }

        return exception.Message.StartsWith(
            "The lighting change failed and the previous lighting state could not be fully restored.",
            StringComparison.Ordinal)
                ? "rollbackIncomplete"
                : "lightingWrite";
    }

    private static bool TryReadParenthesizedCode(
        string message,
        string prefix,
        out string code)
    {
        code = string.Empty;
        if (!message.StartsWith(prefix, StringComparison.Ordinal)
            || !message.EndsWith(").", StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = message[prefix.Length..^2];
        if (candidate.Length is 0 or > 64
            || candidate.Any(character => !char.IsLetterOrDigit(character)))
        {
            return false;
        }

        code = candidate;
        return true;
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

    private static IReadOnlyList<ValidationActionSpec> CreateActionSpecs(byte brightness) =>
    [
        new("off", "Off", LightingSettingsFactory.Off(), false),
        new(
            "static",
            "Static blue",
            LightingSettingsFactory.Static(new RgbColor(0, 128, byte.MaxValue), brightness),
            false),
        new(
            "spectrum",
            "Spectrum cycling",
            LightingSettingsFactory.SpectrumCycling(brightness),
            false),
        new(
            "breathingSingle",
            "Breathing single-color green",
            LightingSettingsFactory.BreathingSingle(BaselineGreen, brightness),
            false),
        new(
            "breathingDual",
            "Breathing dual-color red and blue",
            LightingSettingsFactory.BreathingDual(
                new RgbColor(byte.MaxValue, 0, 0),
                new RgbColor(0, 0, byte.MaxValue),
                brightness),
            false),
        new(
            "breathingRandom",
            "Breathing random colors",
            LightingSettingsFactory.BreathingRandom(brightness),
            false),
        new(
            "reactiveShort",
            "Reactive short green",
            LightingSettingsFactory.Reactive(
                BaselineGreen,
                brightness,
                ReactiveLightingDuration.Short),
            true),
        new(
            "reactiveMedium",
            "Reactive medium green",
            LightingSettingsFactory.Reactive(
                BaselineGreen,
                brightness,
                ReactiveLightingDuration.Medium),
            true),
        new(
            "reactiveLong",
            "Reactive long green",
            LightingSettingsFactory.Reactive(
                BaselineGreen,
                brightness,
                ReactiveLightingDuration.Long),
            true),
    ];

    private static bool ActionPassed(
        ProductionLightingValidationActionEvidence action) =>
        action.ApplySucceeded
        && action.BrightnessMatched
        && action.ReactivePulseApplied == action.ReactivePulseRequired
        && action.VisuallyConfirmed
        && action.Failure is null;

    private static bool OwnershipMatches(
        IProductionLightingPort port,
        LightingSettings settings) => port.OwnershipState ==
        (settings.Effect switch
        {
            LightingEffect.Off => LightingRendererOwnershipState.None,
            LightingEffect.Static => LightingRendererOwnershipState.Static,
            LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
            LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
            LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        });

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        out byte brightness)
    {
        brightness = 0;
        return arguments.Count == 4
            && arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            && arguments[1].Equals(ExactTargetConfirmation, StringComparison.Ordinal)
            && arguments[2].Equals("--brightness", StringComparison.Ordinal)
            && byte.TryParse(arguments[3], out brightness)
            && brightness is > 0 and <= 100;
    }

    private static bool ValidBuildIdentity(ProductionLightingBuildIdentity identity) =>
        identity.CommitHash.Length == 40
        && identity.CommitHash.All(Uri.IsHexDigit)
        && identity.InformationalVersion.EndsWith(
            $"+{identity.CommitHash}",
            StringComparison.OrdinalIgnoreCase)
        && identity.ProtocolAssemblySha256.Length == 64
        && identity.ProtocolAssemblySha256.All(Uri.IsHexDigit);

    private static string CreateFailedArtifactPath(
        string successPath,
        DateTimeOffset completedAtUtc)
    {
        string directory = Path.GetDirectoryName(successPath) ?? string.Empty;
        string file = Path.GetFileNameWithoutExtension(successPath);
        string extension = Path.GetExtension(successPath);
        return Path.Combine(
            directory,
            $"{file}.failed-{completedAtUtc:yyyyMMddTHHmmssZ}{extension}");
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
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                cancellationToken).ConfigureAwait(false);
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

    private static IDisposable RegisterConsoleCancellation(Action cancel)
    {
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancel();
        };
        Console.CancelKeyPress += handler;
        return new CancellationRegistration(handler);
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record ValidationActionSpec(
        string Name,
        string Description,
        LightingSettings Settings,
        bool ReactivePulseRequired);

    private sealed class CancellationRegistration(ConsoleCancelEventHandler handler)
        : IDisposable
    {
        private ConsoleCancelEventHandler? handler = handler;

        public void Dispose()
        {
            ConsoleCancelEventHandler? current = Interlocked.Exchange(ref handler, null);
            if (current is not null)
            {
                Console.CancelKeyPress -= current;
            }
        }
    }
}
