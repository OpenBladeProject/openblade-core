namespace OpenBlade.Service;

internal interface IPadlessHyperBoostLifecycleValidationDevice
{
    Task EnablePadlessHyperBoostLifecycleValidationAsync(
        CancellationToken cancellationToken);

    Task EnablePadlessHyperBoostLifecycleValidationOnceAsync(
        CancellationToken cancellationToken);
}
