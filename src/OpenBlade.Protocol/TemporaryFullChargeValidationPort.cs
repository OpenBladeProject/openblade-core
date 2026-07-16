namespace OpenBlade.Protocol;

public interface ITemporaryFullChargeValidationRunFactory
{
    Task<HidSessionResult<ITemporaryFullChargeValidationPort>> OpenAsync(
        CancellationToken cancellationToken);
}

public interface ITemporaryFullChargeValidationPort : IAsyncDisposable
{
    Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> ApplyTemporaryFullChargeAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> CancelTemporaryFullChargeAsync(
        CancellationToken cancellationToken);
}

internal sealed class TemporaryFullChargeValidationPort(TemporaryFullChargeValidationRun run)
    : ITemporaryFullChargeValidationPort
{
    public Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(
        CancellationToken cancellationToken) => run.QueryProtectionAsync(cancellationToken);

    public Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken) => run.QueryTemporaryFullChargeAsync(cancellationToken);

    public Task<HidSessionResult<HidUnit>> ApplyTemporaryFullChargeAsync(
        CancellationToken cancellationToken) => run.ApplyTemporaryFullChargeAsync(cancellationToken);

    public Task<HidSessionResult<HidUnit>> CancelTemporaryFullChargeAsync(
        CancellationToken cancellationToken) => run.CancelTemporaryFullChargeAsync(cancellationToken);

    public ValueTask DisposeAsync() => run.DisposeAsync();
}
