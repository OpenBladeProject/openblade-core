namespace OpenBlade.Tray;

public interface IStartupRegistration
{
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}
