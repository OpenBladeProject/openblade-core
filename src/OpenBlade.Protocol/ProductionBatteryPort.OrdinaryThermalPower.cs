using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ProductionBatteryPort
{
    private readonly Func<PowerSource>? capturePowerSource;
    private readonly bool supportsOrdinaryThermalControlOnUnclassifiedExternalPower;

    private bool CanUseUnclassifiedAdapterResponse(HidSessionResult<AdapterPowerState> result) =>
        supportsOrdinaryThermalControlOnUnclassifiedExternalPower
        && result.UnclassifiedAdapterResponseAvailable
        && session.Disposition == RazerCommandSessionDisposition.Healthy
        && (result.NativeRetirement is null || result.NativeRetirement.IsCompletedSuccessfully)
        && capturePowerSource is not null
        && OrdinaryThermalPowerAdmission.ExternalPresence(capturePowerSource());

    private async Task<PowerSource?> AdmitOrdinaryThermalCommandAsync(
        RazerCommandDefinition command, bool submitted, bool allowBattery,
        CancellationToken cancellationToken)
    {
        if (!supportsOrdinaryThermalControlOnUnclassifiedExternalPower)
        {
            return null;
        }
        PowerSource source = capturePowerSource!();
        if (allowBattery && source == PowerSource.Battery)
        {
            return null;
        }
        if (!IsOrdinaryProductionCommand(command) || source is not
            (PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC))
        {
            throw ThermalWritePowerFailure(submitted,
                "Ordinary thermal commands require a freshly confirmed external source.");
        }
        AdapterPowerState adapter = await QueryThermalWritePowerAsync(
            submitted, cancellationToken, allowUnclassifiedExternal: true).ConfigureAwait(false);
        ThermalWritePowerRequirement? classification = ClassifyThermalWritePower(adapter);
        bool agrees = adapter == AdapterPowerState.Unknown
            ? OrdinaryThermalPowerAdmission.ExternalPresence(source)
            : classification == ThermalWritePowerRequirement.FullPower
                ? source is PowerSource.ExternalUnknown or PowerSource.PluggedIn
                : classification == ThermalWritePowerRequirement.LimitedExternal
                    && source is PowerSource.ExternalUnknown or PowerSource.UsbC;
        if (!agrees || capturePowerSource() != source)
        {
            throw ThermalWritePowerFailure(submitted,
                "The fresh adapter reading and Windows external source do not agree.");
        }
        return source;
    }

    private bool OrdinarySourceUnchanged(PowerSource? source) =>
        source is null || capturePowerSource?.Invoke() == source;

    private static bool IsOrdinaryProductionCommand(RazerCommandDefinition command)
    {
        ReadOnlySpan<byte> p = command.Payload.AsSpan();
        return (command.CommandClass, command.CommandId) switch
        {
            (0x0D, 0x02) => p.Length == 4 && p[0] == 1 && p[1] is 1 or 2
                && p[2] is 0 or 2 or 4 or 5 && p[3] <= 1,
            (0x0D, 0x01) => p.Length == 3 && p[0] == 1 && p[1] is 1 or 2,
            (0x0D, 0x07) => p.Length == 3 && p[0] == 1 && p[1] is 1 or 2
                && (p[1] == 1 ? p[2] <= 3 : p[2] <= 2),
            _ => false,
        };
    }
}
