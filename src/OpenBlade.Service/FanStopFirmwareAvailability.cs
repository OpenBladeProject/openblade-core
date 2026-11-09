using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record FanStopFirmwareAvailabilityResult(FanStopUnavailableReason Reason, string? Message = null);

// Availability is separate from static source evidence and grants no hardware
// authority. Retry all OS preflight outcomes; never latch a transient failure
// for the service lifetime. Entry forces a fresh probe before reserving an owner.
internal sealed class FanStopFirmwareAvailability(
    Func<FanStopModelContract?> readFamily,
    Func<FanStopModelContract, So6120ServiceFirmwareMetadata> readFirmware,
    TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly object sync = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private FanStopFirmwareAvailabilityResult? last;
    private long lastTimestamp;

    internal static FanStopFirmwareAvailability CreateWindows() => new(
        FanStopModelContract.ForCurrentWindowsHost, So6120WindowsSupervisedMetadata.CaptureFirmwareForService);

    internal FanStopFirmwareAvailabilityResult Read(bool refresh = false)
    {
        lock (sync)
        {
            if (!refresh && last is not null && clock.GetElapsedTime(lastTimestamp) < RefreshInterval) { return last; }
            try
            {
                FanStopModelContract? family = readFamily();
                if (family is null)
                {
                    last = new(FanStopUnavailableReason.UnsupportedDevice,
                        "Fan Stop is unavailable: no reviewed model matches the current host.");
                }
                else
                {
                    So6120ServiceFirmwareMetadata metadata = readFirmware(family);
                    if (metadata.ProcessArchitecture != Architecture.X64 || !family.MatchesHost(metadata.Host))
                    { last = new(FanStopUnavailableReason.RecoveryNotValidated, MetadataUnavailable); }
                    else if (metadata.ReportedEcVersion is null)
                    { last = new(FanStopUnavailableReason.RecoveryNotValidated, MetadataUnavailable); }
                    else if (FanStopModelContract.ForFirmware(family.Model, metadata.ReportedEcVersion) is null)
                    {
                        last = new(FanStopUnavailableReason.RecoveryNotValidated,
                            $"Fan Stop is unavailable: reported EC firmware {metadata.ReportedEcVersion} has no reviewed contract for {family.Model}.");
                    }
                    else { last = new(FanStopUnavailableReason.None); }
                }
            }
            catch { last = new(FanStopUnavailableReason.RecoveryNotValidated, MetadataUnavailable); }
            lastTimestamp = clock.GetTimestamp();
            return last;
        }
    }

    private const string MetadataUnavailable =
        "Fan Stop is unavailable: firmware compatibility metadata could not be read. Availability will be checked again.";
}
