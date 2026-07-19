using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Session;

public sealed class DisplayAutomationCheckpointStore(string path)
{
    private const int SchemaVersion = 1;
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

    private sealed record Checkpoint(
        int SchemaVersion,
        PowerSource LastReconciledPowerSource);
}
