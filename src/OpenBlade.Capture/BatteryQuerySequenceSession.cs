using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public enum BatteryQuerySequenceFailure
{
    None,
    NotAttempted,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    QueryFailed,
    UndefinedSetting,
}

public sealed record BatteryQuerySequenceOrdinalResult(
    BatteryValidationSetting? Setting,
    BatteryQuerySequenceFailure Failure)
{
    public static BatteryQuerySequenceOrdinalResult NotAttempted { get; } =
        new(null, BatteryQuerySequenceFailure.NotAttempted);

    public HidSessionFailureDiagnostic? Diagnostic { get; init; }

    public bool Success =>
        Setting is not null && Failure == BatteryQuerySequenceFailure.None;
}

public sealed record BatteryQuerySequenceResult(
    BatteryQuerySequenceOrdinalResult Query1,
    BatteryQuerySequenceOrdinalResult Query2,
    BatteryQuerySequenceOrdinalResult Query3,
    BatteryQuerySequenceOrdinalResult Query4,
    BatteryQuerySequenceOrdinalResult Query5,
    int CompletedCount)
{
    public bool Success =>
        CompletedCount == 5 &&
        Query1.Success &&
        Query2.Success &&
        Query3.Success &&
        Query4.Success &&
        Query5.Success;
}

public sealed class BatteryQuerySequenceSession
{
    private static readonly TimeSpan InterQueryDelay = TimeSpan.FromSeconds(1);

    private readonly IBatteryQuerySequenceRunFactory factory;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;

    public BatteryQuerySequenceSession(
        IBatteryQuerySequenceRunFactory factory,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<BatteryQuerySequenceResult> RunAsync(
        CancellationToken cancellationToken)
    {
        HidSessionResult<IBatteryQuerySequencePort> opened;
        try
        {
            opened = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return OpenFailure(BatteryQuerySequenceFailure.OpenFailed, null);
        }

        if (opened.Value is not { } port)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OpenFailure(MapOpenFailure(opened.Failure), opened.Diagnostic);
        }

        await using (port.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.Failure is not null)
            {
                return OpenFailure(MapOpenFailure(opened.Failure), opened.Diagnostic);
            }

            BatteryQuerySequenceOrdinalResult[] results =
                Enumerable.Repeat(BatteryQuerySequenceOrdinalResult.NotAttempted, 5).ToArray();
            int completedCount = 0;

            for (int index = 0; index < results.Length; index++)
            {
                HidSessionResult<BatteryValidationSetting> query;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    query = await port.QueryAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    results[index] = Failure(BatteryQuerySequenceFailure.QueryFailed);
                    completedCount++;
                    return CreateResult(results, completedCount);
                }

                completedCount++;
                if (query.Failure is not null)
                {
                    results[index] = Failure(BatteryQuerySequenceFailure.QueryFailed) with
                    {
                        Diagnostic = query.Diagnostic,
                    };
                    return CreateResult(results, completedCount);
                }

                if (!Enum.IsDefined(query.Value))
                {
                    results[index] = Failure(BatteryQuerySequenceFailure.UndefinedSetting);
                    return CreateResult(results, completedCount);
                }

                results[index] = new(query.Value, BatteryQuerySequenceFailure.None);
                if (index < results.Length - 1)
                {
                    await delayAsync(InterQueryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            return CreateResult(results, completedCount);
        }
    }

    private static BatteryQuerySequenceFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => BatteryQuerySequenceFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => BatteryQuerySequenceFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                BatteryQuerySequenceFailure.ConflictDetectionFailed,
            _ => BatteryQuerySequenceFailure.OpenFailed,
        };

    private static BatteryQuerySequenceOrdinalResult Failure(
        BatteryQuerySequenceFailure failure) => new(null, failure);

    private static BatteryQuerySequenceResult OpenFailure(
        BatteryQuerySequenceFailure failure,
        HidSessionFailureDiagnostic? diagnostic) =>
        new(
            Failure(failure) with { Diagnostic = diagnostic },
            BatteryQuerySequenceOrdinalResult.NotAttempted,
            BatteryQuerySequenceOrdinalResult.NotAttempted,
            BatteryQuerySequenceOrdinalResult.NotAttempted,
            BatteryQuerySequenceOrdinalResult.NotAttempted,
            CompletedCount: 0);

    private static BatteryQuerySequenceResult CreateResult(
        IReadOnlyList<BatteryQuerySequenceOrdinalResult> results,
        int completedCount) =>
        new(results[0], results[1], results[2], results[3], results[4], completedCount);
}
