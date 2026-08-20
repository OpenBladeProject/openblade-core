namespace OpenBlade.Service;

internal interface IPadlessHyperBoostLifecycleValidationDevice
{
    Task EnablePadlessHyperBoostLifecycleValidationAsync(
        CancellationToken cancellationToken);
}
