using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public enum FanRpmQueryFailure
{
    None,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    QueryFailed,
}

public sealed record FanRpmQueryResult(
    FanRpmReadings? Readings,
    FanRpmQueryFailure Failure)
{
    public HidSessionFailureDiagnostic? Diagnostic { get; init; }

    public bool Success => Readings is not null && Failure == FanRpmQueryFailure.None;
}

public sealed class FanRpmQuerySession(IFanRpmQueryRunFactory factory)
{
    public async Task<FanRpmQueryResult> RunAsync(CancellationToken cancellationToken)
    {
        HidSessionResult<IFanRpmQueryPort> opened;
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
            return Failure(FanRpmQueryFailure.OpenFailed);
        }

        if (opened.Value is not { } port)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(MapOpenFailure(opened.Failure)) with
            {
                Diagnostic = opened.Diagnostic,
            };
        }

        await using (port.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.Failure is not null)
            {
                return Failure(MapOpenFailure(opened.Failure)) with
                {
                    Diagnostic = opened.Diagnostic,
                };
            }

            HidSessionResult<FanRpmReadings> query;
            try
            {
                query = await port.QueryAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failure(FanRpmQueryFailure.QueryFailed);
            }

            return query.Failure is null && query.Value is not null
                ? new(query.Value, FanRpmQueryFailure.None)
                : Failure(FanRpmQueryFailure.QueryFailed) with
                {
                    Diagnostic = query.Diagnostic,
                };
        }
    }

    private static FanRpmQueryFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => FanRpmQueryFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => FanRpmQueryFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                FanRpmQueryFailure.ConflictDetectionFailed,
            _ => FanRpmQueryFailure.OpenFailed,
        };

    private static FanRpmQueryResult Failure(FanRpmQueryFailure failure) =>
        new(null, failure);
}
