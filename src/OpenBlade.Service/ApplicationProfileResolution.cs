using OpenBlade.Contracts;
using OpenBlade.Core.Native;

namespace OpenBlade.Service;

internal static class ApplicationProfileResolution
{
    internal static PowerProfile Apply(
        PowerProfile baseline,
        ApplicationPowerProfileOverride? value)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (value is null)
        {
            return baseline;
        }

        NativeProfileResult result = ProductNativeRuntime.Policy.Apply(baseline, value);
        if (result.Error is not null)
        {
            // Preserve the legacy constructor's exception and parameter name only
            // for its original synthesis branch; inherited raw enums stay opaque.
            if (result.Error.Kind == NativeBoundaryErrorKind.InvalidArgument
                && result.Error.Detail == 3
                && value.FanControl is null && value.Fans is FanMode mode
                && !Enum.IsDefined(mode))
            {
                _ = FanControlSettings.FromLegacy(mode);
            }
            throw new NativeComponentException(result.Error);
        }
        return result.Profile ?? throw new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult);
    }

    internal static DisplayProfileSettings ApplyDisplay(
        DisplayProfileSettings baseline,
        DisplayProfileSettings? value)
    {
        if (value is null)
        {
            return baseline;
        }

        // The former ?? expressions only dereferenced a null baseline when an
        // override field was absent. Retain that legacy branch and exception.
        if (baseline is null)
        {
            if (value.RefreshRateKey is null || value.HdrEnabled is null
                || value.ColorProfileKey is null || value.BrightnessPercent is null)
            {
                throw new NullReferenceException();
            }
            baseline = DisplayProfileSettings.Unconfigured();
        }

        NativeDisplayResult result = ProductNativeRuntime.Policy.ApplyDisplay(baseline, value);
        if (result.Error is not null)
        {
            throw new NativeComponentException(result.Error);
        }
        return result.Display ?? throw new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult);
    }
}
