using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public enum BatteryQueryFailure
{
    None,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    QueryFailed,
    UndefinedSetting,
}

public sealed record BatteryQueryResult(
    BatteryValidationSetting? Setting,
    BatteryQueryFailure Failure)
{
    public HidSessionFailureDiagnostic? Diagnostic { get; init; }

    public bool Success => Setting is not null && Failure == BatteryQueryFailure.None;
}

public sealed class BatteryQuerySession(IBatteryValidationRunFactory factory)
{
    public async Task<BatteryQueryResult> RunAsync(CancellationToken cancellationToken)
    {
        HidSessionResult<IBatteryValidationPort> opened;
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
            return Failure(BatteryQueryFailure.OpenFailed);
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

            HidSessionResult<BatteryValidationSetting> query;
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
                return Failure(BatteryQueryFailure.QueryFailed);
            }

            if (query.Failure is not null)
            {
                return Failure(BatteryQueryFailure.QueryFailed) with
                {
                    Diagnostic = query.Diagnostic,
                };
            }

            return Enum.IsDefined(query.Value)
                ? new BatteryQueryResult(query.Value, BatteryQueryFailure.None)
                : Failure(BatteryQueryFailure.UndefinedSetting);
        }
    }

    private static BatteryQueryFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => BatteryQueryFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => BatteryQueryFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                BatteryQueryFailure.ConflictDetectionFailed,
            _ => BatteryQueryFailure.OpenFailed,
        };

    private static BatteryQueryResult Failure(BatteryQueryFailure failure) =>
        new(null, failure);
}
