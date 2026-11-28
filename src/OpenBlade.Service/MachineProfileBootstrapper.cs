namespace OpenBlade.Service;

public sealed class MachineProfileBootstrapper
{
    private readonly BatteryConfigurationStore store;
    private readonly MachineConfigurationWriterBinding? bootstrapWriter;

    public MachineProfileBootstrapper(BatteryConfigurationStore store) => this.store = store;

    // The provider must pin the exact existing, protected directory and acquire
    // its enrolled exclusive writer lock without creating or modifying it.
    // Production composition uses the public constructor until that is verified.
    internal MachineProfileBootstrapper(
        BatteryConfigurationStore store,
        string machineRoot,
        Func<string, CancellationToken, IDisposable> acquireVerifiedWriterLease)
        : this(store)
    {
        bootstrapWriter = new MachineConfigurationWriterBinding(machineRoot, acquireVerifiedWriterLease);
    }

    internal Task<BatteryConfiguration> InitializeTransitionAsync(
        MachineConfigurationWriterBinding binding,
        UserConfigurationWriterScope scope,
        CancellationToken cancellationToken) =>
        store.LoadForMachineTransitionAsync(binding, bootstrapWriter, scope, cancellationToken);

    public Task<BatteryConfiguration> InitializeAsync(CancellationToken cancellationToken) =>
        bootstrapWriter is null
            ? store.LoadAsync(cancellationToken)
            : store.LoadForMachineBootstrapAsync(bootstrapWriter, cancellationToken);
}
