namespace OpenBlade.Service;

public sealed class MachineProfileBootstrapper(BatteryConfigurationStore store)
{
    public Task<BatteryConfiguration> InitializeAsync(CancellationToken cancellationToken) =>
        store.LoadAsync(cancellationToken);
}
