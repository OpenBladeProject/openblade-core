using System.ComponentModel;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record Rz090528LightingValidationAction(
    string Name,
    LightingEffect Effect,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool TransitionAcknowledged,
    int ExpectedBrightnessRaw,
    int? ObservedBrightnessRaw,
    bool BrightnessMatched,
    LightingRendererOwnershipState Ownership,
    bool VisuallyConfirmed,
    string? Failure);

internal sealed record Rz090528LightingValidationEvidence(
    int SchemaVersion,
    string EvidenceKind,
    DeviceIdentity Device,
    string InformationalVersion,
    string CommitHash,
    string ProtocolAssemblySha256,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<Rz090528LightingValidationAction> Actions,
    bool MatrixReadbackAvailable,
    bool RestorationAttempted,
    int? RestoredBrightnessRaw,
    bool RestoredBrightnessMatched,
    bool RestoredOwnershipMatched,
    bool RestoredVisuallyConfirmed,
    bool RestoredStaticGreen50,
    string? Failure);

internal sealed class Rz090528LightingValidationCommand
{
    internal const string ExactTargetConfirmation = "RZ09-0528:1532:02C6:2.02";
    internal const int UsageExitCode = 1;
    internal const int ValidationFailureExitCode = 2;
    internal const int CanceledExitCode = 4;
    internal const byte ValidationBrightness = 50;

    private static readonly RgbColor BaselineGreen = new(0, byte.MaxValue, 0);
    private static readonly LightingSettings Baseline =
        LightingSettingsFactory.Static(BaselineGreen, ValidationBrightness);
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
    private readonly ProductionLightingBuildIdentity buildIdentity;
    private readonly string artifactPath;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<string, string, CancellationToken, Task> writeArtifact;
    private readonly Func<Action, IDisposable> registerCancellation;
    private readonly Func<string?> checkEnvironment;

    internal Rz090528LightingValidationCommand(
        Func<CancellationToken, Task<HidSessionResult<IProductionLightingPort>>> openPort,
        Func<CancellationToken,
            Task<HidSessionResult<LightingBrightnessReading>>> queryBrightness,
        TextReader input,
        TextWriter output,
        ProductionLightingBuildIdentity buildIdentity,
        string artifactPath,
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
        this.buildIdentity = buildIdentity
            ?? throw new ArgumentNullException(nameof(buildIdentity));
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        this.artifactPath = artifactPath;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.writeArtifact = writeArtifact ?? WriteAtomicallyAsync;
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
        this.checkEnvironment = checkEnvironment ?? CheckWindowsEnvironment;
    }

    internal static Rz090528LightingValidationCommand CreateWindows(
        TextReader input,
        TextWriter output)
    {
        IProductionLightingPortFactory factory =
            BatteryValidationFactory.CreateWindowsRz090528LightingValidation();
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        return new(
            factory.OpenAsync,
            cancellationToken => BatteryValidationFactory
                .CreateWindowsRz090528LightingBrightnessQuery()
                .RunAsync(cancellationToken),
            input,
            output,
            ProductionLightingEvidenceSource.ReadCurrentBuildIdentity(),
            Path.Combine(
                root,
                "OpenBlade",
                "rz09-0528-02c6-lighting-validation.json"));
    }

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(ExactTargetConfirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-rz09-0528-02c6-lighting " +
                $"--confirm-target {ExactTargetConfirmation}").ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        DateTimeOffset startedAtUtc = utcNow();
        var actions = new List<Rz090528LightingValidationAction>();
        IProductionLightingPort? port = null;
        bool hardwareTouched = false;
        bool restorationAttempted = false;
        int? restoredBrightnessRaw = null;
        bool restoredBrightnessMatched = false;
        bool restoredOwnershipMatched = false;
        bool restoredVisuallyConfirmed = false;
        string? failure = null;
        bool canceled = false;

        try
        {
            if (checkEnvironment() is { } environmentFailure)
            {
                failure = "unsafeValidationEnvironment";
                await output.WriteLineAsync(environmentFailure).ConfigureAwait(false);
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
                else if (!port.Capabilities.LightingEffects.SetEquals(
                    [
                        LightingEffect.Off,
                        LightingEffect.Spectrum,
                        LightingEffect.Static,
                    ]))
                {
                    failure = "exactEffectsNotAdmitted";
                }
                else
                {
                    hardwareTouched = true;
                    await port.EnsureDriverModeAsync(commandCancellation.Token)
                        .ConfigureAwait(false);
                    actions.Add(await ApplyAndConfirmAsync(
                        port,
                        "staticGreen50",
                        Baseline,
                        "Is the entire keyboard Static green at 50%?",
                        commandCancellation.Token).ConfigureAwait(false));
                    if (Passed(actions[^1]))
                    {
                        actions.Add(await ApplyAndConfirmAsync(
                            port,
                            "spectrum50",
                            LightingSettingsFactory.SpectrumCycling(
                                ValidationBrightness),
                            "Is the keyboard cycling smoothly through Spectrum colors at 50%?",
                            commandCancellation.Token).ConfigureAwait(false));
                    }

                    if (actions.Any(action => !Passed(action)))
                    {
                        failure = actions.First(action => !Passed(action)).Failure
                            ?? "visualConfirmationRejected";
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
            failure = $"validationFailed:{ExceptionKind(exception)}";
            await output.WriteLineAsync(
                $"Validation diagnostic: {failure}").ConfigureAwait(false);
        }
        finally
        {
            if (port is not null && hardwareTouched)
            {
                restorationAttempted = true;
                try
                {
                    await port.SetLightingAsync(Baseline, CancellationToken.None)
                        .ConfigureAwait(false);
                    (restoredBrightnessRaw, restoredBrightnessMatched) =
                        await QueryBrightnessAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    restoredOwnershipMatched =
                        port.OwnershipState == LightingRendererOwnershipState.Static;
                    restoredVisuallyConfirmed = !canceled
                        && await PromptYesAsync(
                            "Was Static green at 50% restored across the entire keyboard?",
                            CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    string restoreFailure =
                        $"restoreFailed:{ExceptionKind(exception)}";
                    failure = failure is null
                        ? restoreFailure
                        : $"{failure}|{restoreFailure}";
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
                    failure = failure is null
                        ? "disposeFailed"
                        : $"{failure}|disposeFailed";
                }
            }
        }

        bool restored = restorationAttempted
            && restoredBrightnessMatched
            && restoredOwnershipMatched
            && restoredVisuallyConfirmed;
        bool passed = actions.Count == 2
            && actions.All(Passed)
            && restored
            && failure is null;
        failure ??= passed ? null : "hardwareGatesIncomplete";
        var evidence = new Rz090528LightingValidationEvidence(
            SchemaVersion: 1,
            EvidenceKind: "rz090528ExactMatrixLightingValidation",
            new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"),
            buildIdentity.InformationalVersion,
            buildIdentity.CommitHash,
            buildIdentity.ProtocolAssemblySha256,
            startedAtUtc,
            CompletedAtUtc: utcNow(),
            actions,
            MatrixReadbackAvailable: false,
            restorationAttempted,
            restoredBrightnessRaw,
            restoredBrightnessMatched,
            restoredOwnershipMatched,
            restoredVisuallyConfirmed,
            RestoredStaticGreen50: restored,
            failure);

        try
        {
            await writeArtifact(
                artifactPath,
                JsonSerializer.Serialize(evidence, JsonOptions),
                CancellationToken.None).ConfigureAwait(false);
            await output.WriteLineAsync($"Validation result: {artifactPath}")
                .ConfigureAwait(false);
        }
        catch
        {
            await output.WriteLineAsync("Validation result could not be written.")
                .ConfigureAwait(false);
            return ValidationFailureExitCode;
        }

        return passed
            ? 0
            : canceled
                ? CanceledExitCode
                : ValidationFailureExitCode;
    }

    private async Task<Rz090528LightingValidationAction> ApplyAndConfirmAsync(
        IProductionLightingPort port,
        string name,
        LightingSettings settings,
        string prompt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = utcNow();
        int expectedRaw = LightingBrightnessProtocol.PercentToRaw(settings.Brightness);
        int? observedRaw = null;
        bool acknowledged = false;
        bool brightnessMatched = false;
        bool visuallyConfirmed = false;
        string? failure = null;
        try
        {
            await output.WriteLineAsync($"Applying {name}...").ConfigureAwait(false);
            await port.SetLightingAsync(settings, cancellationToken).ConfigureAwait(false);
            acknowledged = true;
            (observedRaw, brightnessMatched) =
                await QueryBrightnessAsync(cancellationToken).ConfigureAwait(false);
            if (!brightnessMatched)
            {
                failure = "brightnessReadbackMismatch";
            }
            else
            {
                visuallyConfirmed = await PromptYesAsync(prompt, cancellationToken)
                    .ConfigureAwait(false);
                if (!visuallyConfirmed)
                {
                    failure = "visualConfirmationRejected";
                }
            }
        }
        catch (Exception exception)
        {
            failure = $"actionFailed:{ExceptionKind(exception)}";
        }

        return new(
            name,
            settings.Effect,
            startedAtUtc,
            CompletedAtUtc: utcNow(),
            acknowledged,
            expectedRaw,
            observedRaw,
            brightnessMatched,
            port.OwnershipState,
            visuallyConfirmed,
            failure);
    }

    private async Task<(int? ObservedRaw, bool Matched)> QueryBrightnessAsync(
        CancellationToken cancellationToken)
    {
        HidSessionResult<LightingBrightnessReading> result =
            await queryBrightness(cancellationToken).ConfigureAwait(false);
        int? observed = result.Failure is null ? result.Value?.RawValue : null;
        return (
            observed,
            observed == LightingBrightnessProtocol.PercentToRaw(
                ValidationBrightness));
    }

    private async Task<bool> PromptYesAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"{prompt} Type YES or NO:")
            .ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(response?.Trim(), "YES", StringComparison.Ordinal);
    }

    private static bool Passed(Rz090528LightingValidationAction action) =>
        action.TransitionAcknowledged
        && action.BrightnessMatched
        && action.VisuallyConfirmed
        && action.Failure is null
        && action.Ownership == action.Effect switch
        {
            LightingEffect.Static => LightingRendererOwnershipState.Static,
            LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
            _ => LightingRendererOwnershipState.None,
        };

    private static string ExceptionKind(Exception exception) => exception switch
    {
        BladeWriteException write =>
            $"bladeWrite.{write.SubmissionState}.{write.InnerException?.GetType().Name ?? "none"}",
        System.TimeoutException => "timeout",
        UnauthorizedAccessException => "accessDenied",
        IOException => "io",
        ObjectDisposedException => "disposed",
        InvalidOperationException => "invalidOperation",
        _ => "unexpected",
    };

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
}
