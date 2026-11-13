using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    public Task<IndependentLightingSettings?> ReadIndependentLightingAsync(CancellationToken token) =>
        ReadAvailableIndependentLightingAsync(includeBrightness: true, includeLogo: true, token);

    public Task<IndependentLightingSettings?> ReadIndependentLightingAsync(
        IndependentLightingSettings fields, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (!fields.IsWellFormed)
        {
            throw new ArgumentOutOfRangeException(nameof(fields));
        }
        return ReadAvailableIndependentLightingAsync(fields.Brightness.HasValue, fields.LogoMode.HasValue, token);
    }

    private async Task<IndependentLightingSettings?> ReadAvailableIndependentLightingAsync(
        bool includeBrightness, bool includeLogo, CancellationToken token)
    {
        if (!SupportsIndependentLighting(model))
        {
            return null;
        }
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte? brightness = null;
            LogoLightingMode? logo = null;
            if (includeBrightness)
            {
                try
                {
                    brightness = checked((byte)(await ReadBrightnessAsync(token).ConfigureAwait(false) * 100 / 255));
                }
                catch (IOException exception) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
                {
                    RecordQuery("KeyboardBrightness", "Unavailable", failure: exception.GetType().Name);
                }
            }
            if (includeLogo && SupportsExperimentalLogo(model))
            {
                try
                {
                    logo = await ReadLogoAsync(token).ConfigureAwait(false);
                }
                catch (IOException exception) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
                {
                    RecordQuery("LogoLighting", "Unavailable", failure: exception.GetType().Name);
                }
            }
            return brightness.HasValue || logo.HasValue ? new(brightness, logo) : null;
        }
        finally
        {
            operationGate.Release();
        }
    }
    private async Task<IndependentLightingSettings> ReadIndependentLightingCoreAsync(CancellationToken token) => new(
        checked((byte)(await ReadBrightnessAsync(token).ConfigureAwait(false) * 100 / 255)),
        SupportsExperimentalLogo(model) ? await ReadLogoAsync(token).ConfigureAwait(false) : null);

    public async Task SetIndependentLightingAsync(IndependentLightingSettings value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        recoveryWriteGate.ThrowIfBlocked();
        if (!SupportsIndependentLighting(model) || !value.IsWellFormed
            || value.LogoMode.HasValue && !SupportsExperimentalLogo(model))
        {
            throw new CapabilityUnavailableException("The independent lighting request is outside this model's reviewed contract.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            RequireWriteAdmission();
            if (value.Brightness is { } percent)
            {
                // Each admitted product uses ceil(percent * 255 / 100), with floor on
                // readback. Retain the actual raw baseline, never an inferred effect.
                byte raw = checked((byte)((percent * 255 + 99) / 100));
                byte baseline = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
                if (baseline != raw)
                {
                    RequireWriteAdmission();
                    await WriteIndependentLightingCommandAsync(0x03, [1, 5, raw], cancellationToken).ConfigureAwait(false);
                    submitted = true;
                    if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false) != raw)
                    {
                        throw new IOException("Independent keyboard brightness readback did not confirm the requested raw value.");
                    }
                }
            }
            if (value.LogoMode is { } logo && await ReadLogoAsync(cancellationToken).ConfigureAwait(false) != logo)
            {
                byte storage = LogoStorageSelector(model);
                if (logo != LogoLightingMode.Off)
                {
                    byte[] effect = LogoLightingProtocol.SetEffectArguments(logo, storage);
                    RequireWriteAdmission();
                    await WriteIndependentLightingCommandAsync(0x02, effect, cancellationToken).ConfigureAwait(false);
                    submitted = true;
                    ReadOnlyMemory<byte> read = await ExchangeAsync("LogoLighting", 0x03, 0x82,
                        LogoLightingProtocol.QueryArguments(storage), false, cancellationToken).ConfigureAwait(false);
                    if (!read.Span.SequenceEqual(effect))
                    {
                        throw new IOException("Independent logo effect readback did not confirm the requested effect.");
                    }
                }
                RequireWriteAdmission();
                await WriteIndependentLightingCommandAsync(0x00,
                    LogoLightingProtocol.SetStateArguments(logo, storage), cancellationToken).ConfigureAwait(false);
                submitted = true;
                if (await ReadLogoAsync(cancellationToken).ConfigureAwait(false) != logo)
                {
                    throw new IOException("Independent logo readback did not confirm the requested mode.");
                }
            }
            policy.Record("IndependentLighting", "Readback", "Confirmed", submitted,
                requested: $"Brightness={value.Brightness};Logo={value.LogoMode}",
                confirmed: $"Brightness={value.Brightness};Logo={value.LogoMode}");
        }
        catch (Exception exception)
        {
            throw MapWriteFailure("IndependentLighting", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    // The exact inherited numeric LED setters accept a correlated success ACK;
    // only the independent GETs above confirm the requested field. Never replay.
    private Task<ReadOnlyMemory<byte>> WriteIndependentLightingCommandAsync(
        byte commandId, byte[] arguments, CancellationToken token) =>
        ExchangeAsync("IndependentLighting", 0x03, commandId, arguments, true, token,
            variableResponse: true, payloadValidator: value => value.Length <= 3 && value.Span.SequenceEqual(arguments.AsSpan(0, value.Length)), requireEcho: false);
}
