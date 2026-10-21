using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Session;

public sealed class DisplayAutomationCheckpointStore(string path)
{
    private const int SchemaVersion = 1;
    private const int RecoverySchemaVersion = 4;
    private const int MaximumBytes = 4096;
    private readonly object sync = new();

    public PowerSource? Load()
    {
        lock (sync)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is <= 0 or > MaximumBytes)
                {
                    return null;
                }

                Checkpoint? checkpoint = JsonSerializer.Deserialize<Checkpoint>(
                    File.ReadAllBytes(path));
                return checkpoint is
                {
                    SchemaVersion: SchemaVersion,
                    LastReconciledPowerSource: PowerSource.PluggedIn
                            or PowerSource.Battery
                            or PowerSource.UsbC,
                }
                    ? checkpoint.LastReconciledPowerSource
                    : null;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                return null;
            }
        }
    }

    public bool Save(PowerSource source)
    {
        if (source is not (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return false;
        }

        lock (sync)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporaryPath = path + ".tmp";
                File.WriteAllBytes(
                    temporaryPath,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new Checkpoint(SchemaVersion, source)));
                File.Move(temporaryPath, path, overwrite: true);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public DisplayAutomationRecoveryState? LoadRecovery()
    {
        lock (sync)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is <= 0 or > MaximumBytes)
                {
                    return null;
                }
                RecoveryCheckpoint? checkpoint = JsonSerializer.Deserialize<RecoveryCheckpoint>(
                    File.ReadAllBytes(path));
                return checkpoint is
                {
                    SchemaVersion: RecoverySchemaVersion,
                    LastReconciledPowerSource: PowerSource.PluggedIn
                        or PowerSource.Battery
                        or PowerSource.UsbC,
                }
                    ? new DisplayAutomationRecoveryState(
                        checkpoint.LastReconciledPowerSource,
                        checkpoint.ApplicationProfileId,
                        checkpoint.Baseline,
                        checkpoint.OwnedRefreshRate,
                        checkpoint.OwnedHdr,
                        checkpoint.OwnedColorProfile,
                        checkpoint.OwnedBrightness,
                        checkpoint.ManualHold,
                        checkpoint.AutomaticWritesPaused,
                        checkpoint.UncertainRefreshRate,
                        checkpoint.UncertainHdr,
                        checkpoint.UncertainColorProfile,
                        checkpoint.UncertainBrightness)
                    : null;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public bool SaveRecovery(DisplayAutomationRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LastReconciledPowerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return false;
        }
        lock (sync)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(
                    new RecoveryCheckpoint(
                        RecoverySchemaVersion,
                        state.LastReconciledPowerSource,
                        state.ApplicationProfileId,
                        state.Baseline,
                        state.OwnedRefreshRate,
                        state.OwnedHdr,
                        state.OwnedColorProfile,
                        state.OwnedBrightness,
                        state.ManualHold,
                        state.AutomaticWritesPaused,
                        state.UncertainRefreshRate,
                        state.UncertainHdr,
                        state.UncertainColorProfile,
                        state.UncertainBrightness));
                if (encoded.Length > MaximumBytes)
                {
                    return false;
                }
                string temporaryPath = path + ".tmp";
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, path, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private sealed record Checkpoint(
        int SchemaVersion,
        PowerSource LastReconciledPowerSource);

    private sealed record RecoveryCheckpoint(
        int SchemaVersion,
        PowerSource LastReconciledPowerSource,
        Guid? ApplicationProfileId,
        DisplayProfileSettings? Baseline,
        bool OwnedRefreshRate,
        bool OwnedHdr,
        bool OwnedColorProfile,
        bool OwnedBrightness,
        bool ManualHold,
        bool AutomaticWritesPaused,
        bool UncertainRefreshRate,
        bool UncertainHdr,
        bool UncertainColorProfile,
        bool UncertainBrightness);
}

public sealed record DisplayAutomationRecoveryState(
    PowerSource LastReconciledPowerSource,
    Guid? ApplicationProfileId,
    DisplayProfileSettings? Baseline,
    bool OwnedRefreshRate,
    bool OwnedHdr,
    bool OwnedColorProfile,
    bool OwnedBrightness,
    bool ManualHold,
    bool AutomaticWritesPaused,
    bool UncertainRefreshRate,
    bool UncertainHdr,
    bool UncertainColorProfile,
    bool UncertainBrightness);
