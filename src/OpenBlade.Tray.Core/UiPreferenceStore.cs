using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Tray;

public sealed record UiPreferences(
    ThemePreference Theme,
    bool KeepManualChangesUntilCleared = false,
    bool LiveReadingsEnabled = true)
{
    public static UiPreferences Default { get; } = new(
        ThemePreference.FollowWindows,
        KeepManualChangesUntilCleared: false,
        LiveReadingsEnabled: true);
}

public sealed class UiPreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string path;

    public UiPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return UiPreferences.Default;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            UiPreferences? preferences = await JsonSerializer.DeserializeAsync<UiPreferences>(
                stream,
                SerializerOptions,
                cancellationToken);
            return preferences ?? UiPreferences.Default;
        }
        catch (JsonException)
        {
            return UiPreferences.Default;
        }
        catch (IOException)
        {
            return UiPreferences.Default;
        }
    }

    public async Task SaveAsync(
        UiPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    preferences,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
