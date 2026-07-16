using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public enum TemporaryFullChargeQueryFailure
{
    None,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    QueryFailed,
}

public sealed record TemporaryFullChargeQueryResult(
    bool? Active,
    TemporaryFullChargeQueryFailure Failure)
{
    public HidSessionFailureDiagnostic? Diagnostic { get; init; }
    public bool Success => Active is not null && Failure == TemporaryFullChargeQueryFailure.None;
}

public sealed class TemporaryFullChargeQuerySession(
    ITemporaryFullChargeQueryRunFactory factory)
{
    public async Task<TemporaryFullChargeQueryResult> RunAsync(
        CancellationToken cancellationToken)
    {
        HidSessionResult<ITemporaryFullChargeQueryPort> opened;
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
            return Failure(TemporaryFullChargeQueryFailure.OpenFailed);
        }

        if (opened.Value is not { } port)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(MapOpenFailure(opened.Failure));
        }

        await using (port.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.Failure is not null)
            {
                return Failure(MapOpenFailure(opened.Failure));
            }

            HidSessionResult<bool> query;
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
                return Failure(TemporaryFullChargeQueryFailure.QueryFailed);
            }

            return query.Failure is null
                ? new(query.Value, TemporaryFullChargeQueryFailure.None)
                : Failure(TemporaryFullChargeQueryFailure.QueryFailed) with
                {
                    Diagnostic = query.Diagnostic,
                };
        }
    }

    private static TemporaryFullChargeQueryFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => TemporaryFullChargeQueryFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => TemporaryFullChargeQueryFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                TemporaryFullChargeQueryFailure.ConflictDetectionFailed,
            _ => TemporaryFullChargeQueryFailure.OpenFailed,
        };

    private static TemporaryFullChargeQueryResult Failure(
        TemporaryFullChargeQueryFailure failure) => new(null, failure);
}
