namespace OpenBlade.Protocol;

public interface IBatteryQuerySequenceRunFactory
{
    Task<HidSessionResult<IBatteryQuerySequencePort>> OpenAsync(
        CancellationToken cancellationToken);
}

public interface IBatteryQuerySequencePort : IAsyncDisposable
{
    Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken);
}

internal sealed class BatteryQuerySequencePort(
    BatteryQuerySequenceRun run) : IBatteryQuerySequencePort
{
    public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken) => run.QueryAsync(cancellationToken);

    public ValueTask DisposeAsync() => run.DisposeAsync();
}
