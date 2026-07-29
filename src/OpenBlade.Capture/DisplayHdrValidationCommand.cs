using OpenBlade.Display;

namespace OpenBlade.Capture;

internal static class DisplayHdrValidationCommand
{
    internal static async Task<int> RunAsync(
        IDisplaySettingsController controller,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(output);

        DisplaySettingsSnapshot? before = null;
        DisplaySettingsSnapshot? hdrOn = null;
        DisplaySettingsSnapshot? restored = null;
        bool enableAttempted = false;
        bool applied = false;
        bool readBack = false;
        bool restorationConfirmed = false;
        string? failure = null;

        try
        {
            before = await controller.QueryAsync(cancellationToken).ConfigureAwait(false);
            if (!before.IsAvailable)
            {
                failure = "The internal display is unavailable.";
            }
            else if (!before.HdrSupported)
            {
                failure = "HDR is not supported for the internal display.";
            }
            else
            {
                if (before.HdrEnabled)
                {
                    applied = true;
                    hdrOn = before;
                }
                else
                {
                    enableAttempted = true;
                    DisplayOperationResult result = await controller
                        .SetHdrAsync(enabled: true, cancellationToken)
                        .ConfigureAwait(false);
                    applied = result.Succeeded;
                    if (!applied)
                    {
                        failure = result.Error ?? "Windows did not enable HDR.";
                    }

                    hdrOn = await controller.QueryAsync(cancellationToken).ConfigureAwait(false);
                }

                readBack = hdrOn is
                {
                    IsAvailable: true,
                    HdrSupported: true,
                    HdrEnabled: true,
                    ColorProfilesEnabled: false,
                };
                if (!readBack && failure is null)
                {
                    failure =
                        "Windows did not confirm HDR with SDR color-profile controls disabled.";
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.Message;
        }
        finally
        {
            if (before is not null)
            {
                try
                {
                    if (!before.HdrEnabled && enableAttempted)
                    {
                        DisplayOperationResult result = await controller
                            .SetHdrAsync(enabled: false, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!result.Succeeded && failure is null)
                        {
                            failure = result.Error ?? "Windows did not restore HDR off.";
                        }
                    }

                    restored = await controller
                        .QueryAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    restorationConfirmed = MatchesSavedState(before, restored);
                    if (!restorationConfirmed && failure is null)
                    {
                        failure = "Windows did not restore the saved display state.";
                    }
                }
                catch (Exception exception)
                {
                    failure = failure is null
                        ? $"Display restoration failed: {exception.Message}"
                        : $"{failure} Display restoration also failed: {exception.Message}";
                }
            }
        }

        bool success = applied && readBack && restorationConfirmed && failure is null;
        await output.WriteLineAsync("MutationPolicy=SaveSetReadbackRestore")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"OriginalHdr={Value(before?.HdrEnabled)}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"HdrOn={Value(hdrOn?.HdrEnabled)}")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
                $"HdrOnColorProfilesEnabled={Value(hdrOn?.ColorProfilesEnabled)}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"RestoredHdr={Value(restored?.HdrEnabled)}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"Applied={applied}").ConfigureAwait(false);
        await output.WriteLineAsync($"ReadBack={readBack}").ConfigureAwait(false);
        await output.WriteLineAsync($"Restored={restorationConfirmed}").ConfigureAwait(false);
        await output.WriteLineAsync($"Failure={failure ?? "None"}").ConfigureAwait(false);
        await output.WriteLineAsync($"Success={success}").ConfigureAwait(false);
        return success ? 0 : 2;
    }

    private static bool MatchesSavedState(
        DisplaySettingsSnapshot expected,
        DisplaySettingsSnapshot actual) =>
        actual.IsAvailable
        && actual.HdrEnabled == expected.HdrEnabled
        && actual.Width == expected.Width
        && actual.Height == expected.Height
        && string.Equals(
            actual.CurrentRefreshKey,
            expected.CurrentRefreshKey,
            StringComparison.Ordinal)
        && string.Equals(
            actual.ActiveColorProfileId,
            expected.ActiveColorProfileId,
            StringComparison.OrdinalIgnoreCase);

    private static string Value(bool? value) => value?.ToString() ?? "Unavailable";
}
