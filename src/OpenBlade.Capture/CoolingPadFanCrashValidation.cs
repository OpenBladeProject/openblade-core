using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadFanCrashFailure
{
    None,
    AlreadyRun,
    Canceled,
    OpenFailed,
    ApplyKnownNotApplied,
    ApplyIndeterminate,
    ApplyPhysicalNotConfirmed,
    RetainedStateNotConfirmed,
    ReleaseKnownNotApplied,
    ReleaseIndeterminate,
    ReleasePhysicalNotConfirmed,
    HandleCloseFailed,
    UnexpectedFailure,
}

internal sealed record CoolingPadFanCrashArmResult(
    bool ApplyAttempted,
    bool ApplyAcknowledged,
    bool ApplyPhysicalConfirmed,
    bool HandleClosedBeforeExit,
    bool ManualSynapseRecoveryRequired,
    bool ManualPowerCycleMayBeRequired,
    CoolingPadFanCrashFailure Failure)
{
    internal bool Success => Failure == CoolingPadFanCrashFailure.None
        && ApplyAttempted
        && ApplyAcknowledged
        && ApplyPhysicalConfirmed
        && HandleClosedBeforeExit
        && !ManualSynapseRecoveryRequired
        && !ManualPowerCycleMayBeRequired;
}

internal sealed record CoolingPadFanCrashRecoveryResult(
    bool Retained2200PhysicallyConfirmed,
    bool ReleaseAttempted,
    bool ReleaseAcknowledged,
    bool ReleasePhysicalConfirmed,
    bool HandleClosed,
    bool RetryAttempted,
    bool ManualSynapseRecoveryRequired,
    bool ManualPowerCycleMayBeRequired,
    CoolingPadFanCrashFailure Failure)
{
    internal bool Success => Failure == CoolingPadFanCrashFailure.None
        && Retained2200PhysicallyConfirmed
        && ReleaseAttempted
        && ReleaseAcknowledged
        && ReleasePhysicalConfirmed
        && HandleClosed
        && !RetryAttempted
        && !ManualSynapseRecoveryRequired
        && !ManualPowerCycleMayBeRequired;
}

internal sealed class CoolingPadFanCrashValidationRunner
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int armUsed;
    private int recoveryUsed;

    internal CoolingPadFanCrashValidationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadFanCrashValidationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadFanCrashArmResult> ArmAsync(
        Func<CancellationToken, Task<bool>> confirm2200,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm2200);
        if (Interlocked.Exchange(ref armUsed, 1) != 0)
        {
            return ArmFailure(CoolingPadFanCrashFailure.AlreadyRun);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return ArmFailure(CoolingPadFanCrashFailure.Canceled);
        }

        ValidationActor? actor = null;
        bool applyAttempted = false;
        bool applyAcknowledged = false;
        bool physicalConfirmed = false;
        bool poisoned = false;
        CoolingPadFanCrashFailure failure = CoolingPadFanCrashFailure.None;
        bool handleClosed = false;
        try
        {
            ValidationRunContext context = createContext();
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                return ArmFailure(cancellationToken.IsCancellationRequested
                    ? CoolingPadFanCrashFailure.Canceled
                    : CoolingPadFanCrashFailure.OpenFailed);
            }

            using var operationGate = new SemaphoreSlim(1, 1);
            var executor = new CoolingPadHostControlExecutor(
                actor.Session,
                context,
                operationGate,
                ExchangeTimeout,
                () => poisoned = true);
            applyAttempted = true;
            try
            {
                await executor.ApplyValidated2200Async(cancellationToken)
                    .ConfigureAwait(false);
                applyAcknowledged = true;
            }
            catch (BladeWriteException exception)
            {
                failure = exception.SubmissionState
                    == WriteSubmissionState.KnownNotApplied
                    ? CoolingPadFanCrashFailure.ApplyKnownNotApplied
                    : CoolingPadFanCrashFailure.ApplyIndeterminate;
                return Result();
            }

            physicalConfirmed = await confirm2200(cancellationToken)
                .ConfigureAwait(false);
            if (!physicalConfirmed)
            {
                failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanCrashFailure.Canceled
                    : CoolingPadFanCrashFailure.ApplyPhysicalNotConfirmed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = CoolingPadFanCrashFailure.Canceled;
        }
        catch
        {
            failure = CoolingPadFanCrashFailure.UnexpectedFailure;
            poisoned |= applyAttempted;
        }
        finally
        {
            if (actor is not null)
            {
                handleClosed = await CloseExactlyAsync(actor).ConfigureAwait(false);
                if (!handleClosed && failure == CoolingPadFanCrashFailure.None)
                {
                    failure = CoolingPadFanCrashFailure.HandleCloseFailed;
                }
            }
        }

        return Result();

        CoolingPadFanCrashArmResult Result() => new(
            applyAttempted,
            applyAcknowledged,
            physicalConfirmed,
            handleClosed,
            ManualSynapseRecoveryRequired:
                applyAcknowledged && (!physicalConfirmed || !handleClosed),
            ManualPowerCycleMayBeRequired: poisoned,
            failure);
    }

    internal async Task<CoolingPadFanCrashRecoveryResult> RecoverAsync(
        Func<CancellationToken, Task<bool>> confirmRetained2200,
        Func<CancellationToken, Task<bool>> confirmRelease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmRetained2200);
        ArgumentNullException.ThrowIfNull(confirmRelease);
        if (Interlocked.Exchange(ref recoveryUsed, 1) != 0)
        {
            return RecoveryFailure(CoolingPadFanCrashFailure.AlreadyRun);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return RecoveryFailure(CoolingPadFanCrashFailure.Canceled);
        }

        bool retained = false;
        bool releaseAttempted = false;
        bool releaseAcknowledged = false;
        bool releaseConfirmed = false;
        bool poisoned = false;
        bool handleClosed = false;
        CoolingPadFanCrashFailure failure = CoolingPadFanCrashFailure.None;
        ValidationActor? actor = null;
        try
        {
            retained = await confirmRetained2200(cancellationToken)
                .ConfigureAwait(false);
            if (!retained)
            {
                return RecoveryFailure(cancellationToken.IsCancellationRequested
                    ? CoolingPadFanCrashFailure.Canceled
                    : CoolingPadFanCrashFailure.RetainedStateNotConfirmed);
            }

            ValidationRunContext context = createContext();
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                failure = cancellationToken.IsCancellationRequested
                    ? CoolingPadFanCrashFailure.Canceled
                    : CoolingPadFanCrashFailure.OpenFailed;
            }
            else
            {
                using var operationGate = new SemaphoreSlim(1, 1);
                var executor = new CoolingPadHostControlExecutor(
                    actor.Session,
                    context,
                    operationGate,
                    ExchangeTimeout,
                    () => poisoned = true);
                releaseAttempted = true;
                try
                {
                    await executor.ReleaseAsync(cancellationToken)
                        .ConfigureAwait(false);
                    releaseAcknowledged = true;
                }
                catch (BladeWriteException exception)
                {
                    failure = exception.SubmissionState
                        == WriteSubmissionState.KnownNotApplied
                        ? CoolingPadFanCrashFailure.ReleaseKnownNotApplied
                        : CoolingPadFanCrashFailure.ReleaseIndeterminate;
                }

                if (releaseAcknowledged)
                {
                    releaseConfirmed = await confirmRelease(cancellationToken)
                        .ConfigureAwait(false);
                    if (!releaseConfirmed)
                    {
                        failure = cancellationToken.IsCancellationRequested
                            ? CoolingPadFanCrashFailure.Canceled
                            : CoolingPadFanCrashFailure.ReleasePhysicalNotConfirmed;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = CoolingPadFanCrashFailure.Canceled;
        }
        catch
        {
            failure = CoolingPadFanCrashFailure.UnexpectedFailure;
            poisoned |= releaseAttempted;
        }
        finally
        {
            if (actor is not null)
            {
                handleClosed = await CloseExactlyAsync(actor).ConfigureAwait(false);
                if (!handleClosed && failure == CoolingPadFanCrashFailure.None)
                {
                    failure = CoolingPadFanCrashFailure.HandleCloseFailed;
                }
            }
        }

        return new CoolingPadFanCrashRecoveryResult(
            retained,
            releaseAttempted,
            releaseAcknowledged,
            releaseConfirmed,
            handleClosed,
            RetryAttempted: false,
            ManualSynapseRecoveryRequired: !releaseConfirmed,
            ManualPowerCycleMayBeRequired: poisoned,
            failure);
    }

    private async Task<bool> CloseExactlyAsync(ValidationActor actor)
    {
        Task disposal;
        try
        {
            disposal = actor.DisposeAsync().AsTask();
        }
        catch
        {
            return false;
        }

        try
        {
            await disposal.WaitAsync(CleanupTimeout, timeProvider)
                .ConfigureAwait(false);
            return disposal.IsCompletedSuccessfully;
        }
        catch
        {
            if (!disposal.IsCompleted)
            {
                pendingDisposals.Retain(disposal);
            }
            return false;
        }
    }

    private static CoolingPadFanCrashArmResult ArmFailure(
        CoolingPadFanCrashFailure failure) => new(
        false, false, false, false, false, false, failure);

    private static CoolingPadFanCrashRecoveryResult RecoveryFailure(
        CoolingPadFanCrashFailure failure) => new(
        false, false, false, false, false, false, true, false, failure);
}

internal enum CoolingPadFanCrashMarkerState
{
    Armed,
    GracefulReleaseCompleted,
    UnexpectedExitReleaseCompleted,
    ManualRecoveryRequired,
}

internal sealed record CoolingPadFanCrashMarker(
    int SchemaVersion,
    string Target,
    string BuildVersion,
    string ExecutableSha256,
    int OriginProcessId,
    long OriginProcessStartUtcTicks,
    CoolingPadFanCrashMarkerState State,
    bool Fixed2200PhysicallyConfirmed,
    bool HandleClosedBeforeExit,
    bool ReleaseAttempted,
    bool ReleasePhysicallyConfirmed,
    bool ManualRecoveryRequired);

internal interface ICoolingPadFanCrashMarkerStore
{
    Task CreateArmedAsync(
        CoolingPadFanCrashMarker marker,
        CancellationToken cancellationToken);

    Task<CoolingPadFanCrashMarker?> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(
        CoolingPadFanCrashMarker marker,
        CancellationToken cancellationToken);
}

internal sealed class CoolingPadFanCrashMarkerStore(string path)
    : ICoolingPadFanCrashMarkerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly string path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A marker path is required.", nameof(path))
            : path);

    public async Task CreateArmedAsync(
        CoolingPadFanCrashMarker marker,
        CancellationToken cancellationToken)
    {
        Validate(marker, requireArmed: true);
        string temporary = TemporaryPath();
        try
        {
            await WriteTemporaryAsync(temporary, marker, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<CoolingPadFanCrashMarker?> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        CoolingPadFanCrashMarker? marker = await JsonSerializer
            .DeserializeAsync<CoolingPadFanCrashMarker>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        Validate(marker, requireArmed: false);
        return marker;
    }

    public async Task WriteAsync(
        CoolingPadFanCrashMarker marker,
        CancellationToken cancellationToken)
    {
        Validate(marker, requireArmed: false);
        string temporary = TemporaryPath();
        try
        {
            await WriteTemporaryAsync(temporary, marker, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task WriteTemporaryAsync(
        string temporary,
        CoolingPadFanCrashMarker marker,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await using FileStream stream = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
                stream,
                marker,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string TemporaryPath() =>
        path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void Validate(CoolingPadFanCrashMarker? marker, bool requireArmed)
    {
        if (marker is not
            {
                SchemaVersion: 1,
                Target: CoolingPadFanCrashValidationCommand.Target,
                BuildVersion.Length: > 0,
                ExecutableSha256.Length: 64,
                OriginProcessId: > 0,
                OriginProcessStartUtcTicks: > 0,
                Fixed2200PhysicallyConfirmed: true,
                HandleClosedBeforeExit: true,
            }
            || !Enum.IsDefined(marker.State)
            || requireArmed && marker.State != CoolingPadFanCrashMarkerState.Armed)
        {
            throw new InvalidDataException(
                "The cooling-pad fan crash marker is malformed or incompatible.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class CoolingPadFanCrashValidationCommand
{
    internal const string Target = "RZ09-0581:1532:0F43:REV-0200:BIOS-4.00";
    internal const string ArmConfirmation =
        "RZ09-0581:4.00:1532:0F43:FAN-2200:FORCED-EXIT:RELEASE-ONCE";
    internal const string RecoveryConfirmation =
        "RZ09-0581:4.00:1532:0F43:ORIGIN-EXITED:" +
        "CONFIRM-2200:RELEASE-ONCE";
    internal const string Fixed2200Confirmation = "FAN-2200-CONFIRMED";
    internal const string Retained2200Confirmation =
        "FAN-2200-RETAINED-AFTER-EXIT";
    internal const string ReleaseConfirmation = "FAN-RELEASE-CONFIRMED";
    internal const int UsageExitCode = 1;
    internal const int ValidationFailureExitCode = 2;
    internal const int ApprovalRejectedExitCode = 3;
    internal const int CanceledExitCode = 4;

    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanCrashArmResult>> arm;
    private readonly Func<Func<CancellationToken, Task<bool>>,
        Func<CancellationToken, Task<bool>>, CancellationToken,
        Task<CoolingPadFanCrashRecoveryResult>> recover;
    private readonly Func<string, ICoolingPadFanCrashMarkerStore> markerStore;
    private readonly Func<CaptureProcessIdentity> processIdentity;
    private readonly Func<int, long, bool> isProcessAlive;
    private readonly Func<CancellationToken, Task> waitForForcedExit;
    private readonly Func<Action, IDisposable> registerCancellation;
    private readonly TextReader input;
    private readonly TextWriter output;

    internal CoolingPadFanCrashValidationCommand(
        Func<Func<CancellationToken, Task<bool>>, CancellationToken,
            Task<CoolingPadFanCrashArmResult>> arm,
        Func<Func<CancellationToken, Task<bool>>,
            Func<CancellationToken, Task<bool>>, CancellationToken,
            Task<CoolingPadFanCrashRecoveryResult>> recover,
        TextReader input,
        TextWriter output,
        Func<string, ICoolingPadFanCrashMarkerStore>? markerStore = null,
        Func<CaptureProcessIdentity>? processIdentity = null,
        Func<int, long, bool>? isProcessAlive = null,
        Func<CancellationToken, Task>? waitForForcedExit = null,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.arm = arm ?? throw new ArgumentNullException(nameof(arm));
        this.recover = recover ?? throw new ArgumentNullException(nameof(recover));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.markerStore = markerStore ?? (path => new CoolingPadFanCrashMarkerStore(path));
        this.processIdentity = processIdentity ?? CaptureProcessIdentity.Current;
        this.isProcessAlive = isProcessAlive ?? IsMatchingProcessAlive;
        this.waitForForcedExit = waitForForcedExit
            ?? (token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
    }

    internal async Task<int> RunArmAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParse(arguments, ArmConfirmation, out string? markerPath))
        {
            await WriteUsageAsync("arm", ArmConfirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(linked));
        await output.WriteLineAsync(
            "WARNING: This developer-only route applies Fixed 2200 once, " +
            "confirms it physically, closes the handle, and waits for you to " +
            "terminate only this Capture process externally. Ctrl+C attempts " +
            "one physically confirmed release instead.").ConfigureAwait(false);
        await output.WriteLineAsync("Type:").ConfigureAwait(false);
        await output.WriteLineAsync(ArmConfirmation).ConfigureAwait(false);
        string? approval = await input.ReadLineAsync(linked.Token).ConfigureAwait(false);
        if (!string.Equals(approval, ArmConfirmation, StringComparison.Ordinal))
        {
            return ApprovalRejectedExitCode;
        }

        CoolingPadFanCrashArmResult armed = await arm(
                token => ConfirmAsync(Fixed2200Confirmation, token),
                linked.Token)
            .ConfigureAwait(false);
        if (!armed.Success)
        {
            CoolingPadFanCrashRecoveryResult? cleanup = armed.ApplyAcknowledged
                ? await RecoverBoundedAsync().ConfigureAwait(false)
                : null;
            await WriteResultAsync(Result.ArmFailure(armed, cleanup))
                .ConfigureAwait(false);
            return ValidationFailureExitCode;
        }

        CaptureProcessIdentity identity = processIdentity();
        var marker = new CoolingPadFanCrashMarker(
            1,
            Target,
            identity.BuildVersion,
            identity.ExecutableSha256,
            identity.ProcessId,
            identity.StartUtcTicks,
            CoolingPadFanCrashMarkerState.Armed,
            Fixed2200PhysicallyConfirmed: true,
            HandleClosedBeforeExit: true,
            ReleaseAttempted: false,
            ReleasePhysicallyConfirmed: false,
            ManualRecoveryRequired: false);
        ICoolingPadFanCrashMarkerStore store = markerStore(markerPath!);
        bool markerCreated = false;
        bool canceled = false;
        try
        {
            await store.CreateArmedAsync(marker, linked.Token).ConfigureAwait(false);
            markerCreated = true;
            await output.WriteLineAsync(
                "MarkerArmed=True. End only this OpenBlade.Capture process " +
                "externally, then run the recovery command with the same marker. " +
                "Do not retry the apply command.").ConfigureAwait(false);
            await waitForForcedExit(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            canceled = true;
        }
        catch
        {
        }

        CoolingPadFanCrashRecoveryResult recovery = await RecoverBoundedAsync()
            .ConfigureAwait(false);
        if (markerCreated)
        {
            CoolingPadFanCrashMarker completed = marker with
            {
                State = recovery.Success
                    ? CoolingPadFanCrashMarkerState.GracefulReleaseCompleted
                    : CoolingPadFanCrashMarkerState.ManualRecoveryRequired,
                ReleaseAttempted = recovery.ReleaseAttempted,
                ReleasePhysicallyConfirmed = recovery.ReleasePhysicalConfirmed,
                ManualRecoveryRequired = !recovery.Success,
            };
            try
            {
                await store.WriteAsync(completed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }
        await WriteResultAsync(Result.Graceful(armed, recovery, markerCreated))
            .ConfigureAwait(false);
        return recovery.Success && canceled
            ? CanceledExitCode
            : ValidationFailureExitCode;
    }

    internal async Task<int> RunRecoveryAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParse(arguments, RecoveryConfirmation, out string? markerPath))
        {
            await WriteUsageAsync("recover", RecoveryConfirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        ICoolingPadFanCrashMarkerStore store = markerStore(markerPath!);
        CoolingPadFanCrashMarker? marker;
        try
        {
            marker = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            marker = null;
        }
        CaptureProcessIdentity current = processIdentity();
        bool markerMatched = marker is
        {
            State: CoolingPadFanCrashMarkerState.Armed,
            Target: Target,
        }
            && string.Equals(marker.BuildVersion, current.BuildVersion,
                StringComparison.Ordinal)
            && string.Equals(marker.ExecutableSha256, current.ExecutableSha256,
                StringComparison.Ordinal);
        bool originExited = markerMatched
            && !isProcessAlive(
                marker!.OriginProcessId,
                marker.OriginProcessStartUtcTicks);
        if (!markerMatched || !originExited)
        {
            await WriteResultAsync(Result.Blocked(markerMatched, originExited))
                .ConfigureAwait(false);
            return ValidationFailureExitCode;
        }

        await output.WriteLineAsync(
            "The exact armed Capture process is no longer running. Confirm that " +
            "the pad still physically holds Fixed 2200 before the one release write.")
            .ConfigureAwait(false);
        await output.WriteLineAsync("Type:").ConfigureAwait(false);
        await output.WriteLineAsync(RecoveryConfirmation).ConfigureAwait(false);
        string? approval = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(approval, RecoveryConfirmation, StringComparison.Ordinal))
        {
            return ApprovalRejectedExitCode;
        }

        CoolingPadFanCrashRecoveryResult recovery = await RecoverBoundedAsync()
            .ConfigureAwait(false);
        bool success = recovery.Success;
        CoolingPadFanCrashMarker completed = marker! with
        {
            State = success
                ? CoolingPadFanCrashMarkerState.UnexpectedExitReleaseCompleted
                : CoolingPadFanCrashMarkerState.ManualRecoveryRequired,
            ReleaseAttempted = recovery.ReleaseAttempted,
            ReleasePhysicallyConfirmed = recovery.ReleasePhysicalConfirmed,
            ManualRecoveryRequired = !success,
        };
        try
        {
            await store.WriteAsync(completed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            success = false;
        }
        await WriteResultAsync(Result.UnexpectedExit(
                markerMatched,
                originExited,
                recovery,
                success))
            .ConfigureAwait(false);
        return success ? 0 : ValidationFailureExitCode;
    }

    private async Task<CoolingPadFanCrashRecoveryResult> RecoverBoundedAsync()
    {
        using var cleanup = new CancellationTokenSource(RecoveryTimeout);
        try
        {
            return await recover(
                    token => ConfirmAsync(Retained2200Confirmation, token),
                    token => ConfirmAsync(ReleaseConfirmation, token),
                    cleanup.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            return new CoolingPadFanCrashRecoveryResult(
                false, false, false, false, false, false, true, false,
                CoolingPadFanCrashFailure.UnexpectedFailure);
        }
    }

    private async Task<bool> ConfirmAsync(
        string expected,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            $"Type {expected} after physical confirmation.").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }

    private Task WriteResultAsync(Result result) => output.WriteLineAsync(
        JsonSerializer.Serialize(result, ResultJsonOptions));

    private Task WriteUsageAsync(string phase, string confirmation) =>
        output.WriteLineAsync(
            $"Usage: OpenBlade.Capture validate-cooling-pad-fan-crash-{phase} " +
            $"--confirm-target {confirmation} --marker <private-marker-path>");

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        string confirmation,
        out string? markerPath)
    {
        markerPath = null;
        if (arguments.Count != 4
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(confirmation, StringComparison.Ordinal)
            || !arguments[2].Equals("--marker", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[3]))
        {
            return false;
        }
        markerPath = arguments[3];
        return true;
    }

    private static bool IsMatchingProcessAlive(int processId, long startUtcTicks)
    {
        try
        {
            using System.Diagnostics.Process process =
                System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == startUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private sealed record Result(
        int SchemaVersion,
        bool ProductionAdmitted,
        bool CrashRecoveryValidated,
        bool ExactBuildMarkerMatched,
        bool OriginProcessExited,
        bool UnexpectedExitObserved,
        bool MarkerCreated,
        CoolingPadFanCrashArmResult? Arm,
        CoolingPadFanCrashRecoveryResult? Recovery,
        bool ActiveModeReadbackConfirmed,
        bool PhysicalRpmReadbackConfirmed,
        bool RetryAttempted,
        bool ManualSynapseRecoveryRequired,
        bool ManualPowerCycleMayBeRequired,
        bool Success)
    {
        internal static Result ArmFailure(
            CoolingPadFanCrashArmResult arm,
            CoolingPadFanCrashRecoveryResult? recovery) => new(
            1, false, false, false, false, false, false, arm, recovery,
            false, false, false,
            recovery?.ManualSynapseRecoveryRequired
                ?? arm.ManualSynapseRecoveryRequired,
            recovery?.ManualPowerCycleMayBeRequired
                ?? arm.ManualPowerCycleMayBeRequired,
            false);

        internal static Result Graceful(
            CoolingPadFanCrashArmResult arm,
            CoolingPadFanCrashRecoveryResult recovery,
            bool markerCreated) => new(
            1, false, false, false, false, false, markerCreated, arm, recovery,
            false, false, false, recovery.ManualSynapseRecoveryRequired,
            recovery.ManualPowerCycleMayBeRequired, false);

        internal static Result Blocked(bool markerMatched, bool originExited) => new(
            1, false, false, markerMatched, originExited, false, false, null,
            null, false, false, false, false, false, false);

        internal static Result UnexpectedExit(
            bool markerMatched,
            bool originExited,
            CoolingPadFanCrashRecoveryResult recovery,
            bool success) => new(
            1, false, success, markerMatched, originExited, true, true, null,
            recovery, false, false, false,
            recovery.ManualSynapseRecoveryRequired,
            recovery.ManualPowerCycleMayBeRequired,
            success);
    }
}
