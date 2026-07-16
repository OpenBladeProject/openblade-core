using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public interface IProductionBatteryPort : IAsyncDisposable
{
    DeviceCapabilities Capabilities { get; }

    bool SupportsTemporaryFullChargeQuery { get; }

    Task<ConfirmedBatteryState> QueryProtectionAsync(CancellationToken cancellationToken);

    Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken);

    Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken);

    Task SetProtectionOffAsync(CancellationToken cancellationToken);

    Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken);
}

public interface IProductionBatteryPortFactory
{
    DeviceCapabilities Capabilities { get; }

    Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
        CancellationToken cancellationToken);
}
