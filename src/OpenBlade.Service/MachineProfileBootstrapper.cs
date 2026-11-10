namespace OpenBlade.Service;

public sealed class MachineProfileBootstrapper
{
    private readonly BatteryConfigurationStore store;
    private readonly string? machineRoot;
    private readonly Func<string, CancellationToken, IDisposable>? acquireVerifiedWriterLease;

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
        this.machineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(machineRoot));
        this.acquireVerifiedWriterLease = acquireVerifiedWriterLease
            ?? throw new ArgumentNullException(nameof(acquireVerifiedWriterLease));
    }

    public Task<BatteryConfiguration> InitializeAsync(CancellationToken cancellationToken) =>
        acquireVerifiedWriterLease is null
            ? store.LoadAsync(cancellationToken)
            : store.LoadForMachineBootstrapAsync(machineRoot!, acquireVerifiedWriterLease, cancellationToken);
}
