using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed partial class ProductionLightingSession
{
    internal async Task<IndependentLightingSettings?> ReadIndependentLightingStateCoreAsync(CancellationToken token, IndependentLightingSettings? fields = null)
    {
        try
        {
            IProductionLightingPort? port = await GetLightingPortCoreAsync(
                LightingPortOperation.KeyboardLightingStateRead, token).ConfigureAwait(false);
            return port is null ? null
                : fields is null ? await port.ReadIndependentLightingAsync(token).ConfigureAwait(false)
                : await port.ReadIndependentLightingAsync(fields, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(nameof(ProductionLightingSession), nameof(ReadIndependentLightingStateCoreAsync), exception);
            // Healthy optional field failures return partial observations in the
            // port. A propagated failure retires this independent (unowned) port.
            await ResetLightingPortCoreAsync(lightingPort).ConfigureAwait(false);
            return null;
        }
    }

    internal async Task SetIndependentLightingAsync(IndependentLightingSettings value, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsWellFormed || !lightingFactory.Capabilities.SupportsIndependentLightingControls
            || value.Brightness.HasValue && !lightingFactory.Capabilities.SupportsKeyboardBrightness
            || value.LogoMode is { } logo && !lightingFactory.Capabilities.LogoLightingModes.Contains(logo))
        {
            throw new CapabilityUnavailableException("The requested independent lighting fields are not admitted.");
        }
        IProductionLightingPort? port = null;
        try
        {
            await enterHardwareOperation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Independent lighting was canceled before a verified session opened.");
        }
        catch (ObjectDisposedException exception)
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Independent lighting was blocked because the device provider is closing.", exception);
        }
        try
        {
            port = await GetLightingPortForWriteCoreAsync(LightingPortOperation.LightingChange, token).ConfigureAwait(false);
            if (!port.Capabilities.SupportsIndependentLightingControls)
            {
                throw new CapabilityUnavailableException("The exact-device port has no independent lighting contract.");
            }
            ThrowIfOrdinaryLightingWriteBlocked();
            await port.SetIndependentLightingAsync(value, token).ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException)
        {
            throw;
        }
        catch (BladeWriteException exception)
        {
            if (!LightingRecoveryWriteGate.IsRejection(exception)
                && exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            }
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "Independent lighting was canceled after a verified session opened.");
        }
        catch (Exception exception)
        {
            await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "The independent lighting write outcome is indeterminate.", exception);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }
}
