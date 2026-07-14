namespace OpenSynapse.Protocol;

public interface IBatteryValidationRunFactory
{
    Task<HidSessionResult<IBatteryValidationPort>> OpenAsync(
        CancellationToken cancellationToken);
}

public interface IBatteryValidationPort : IAsyncDisposable
{
    Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken);

    Task<HidSessionResult<HidUnit>> ApplyAsync(
        BatteryValidationSetting setting,
        CancellationToken cancellationToken);
}

internal sealed class BatteryValidationPort(BatteryValidationRun run) : IBatteryValidationPort
{
    public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken) => run.QueryAsync(cancellationToken);

    public Task<HidSessionResult<HidUnit>> ApplyAsync(
        BatteryValidationSetting setting,
        CancellationToken cancellationToken) => run.ApplyAsync(setting, cancellationToken);

    public ValueTask DisposeAsync() => run.DisposeAsync();
}
