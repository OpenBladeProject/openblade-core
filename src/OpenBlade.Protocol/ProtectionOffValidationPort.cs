namespace OpenBlade.Protocol;

public interface IProtectionOffValidationRunFactory
{
    Task<HidSessionResult<IProtectionOffValidationPort>> OpenAsync(
        CancellationToken cancellationToken);
}

public interface IProtectionOffValidationPort : IAsyncDisposable
{
    Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> ApplyProtectionOffAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> RestoreLimit80Async(
        CancellationToken cancellationToken);
}

internal sealed class ProtectionOffValidationPort(ProtectionOffValidationRun run)
    : IProtectionOffValidationPort
{
    public Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(
        CancellationToken cancellationToken) => run.QueryProtectionAsync(cancellationToken);

    public Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken) => run.QueryTemporaryFullChargeAsync(cancellationToken);

    public Task<HidSessionResult<HidUnit>> ApplyProtectionOffAsync(
        CancellationToken cancellationToken) => run.ApplyProtectionOffAsync(cancellationToken);

    public Task<HidSessionResult<HidUnit>> RestoreLimit80Async(
        CancellationToken cancellationToken) => run.RestoreLimit80Async(cancellationToken);

    public ValueTask DisposeAsync() => run.DisposeAsync();
}
