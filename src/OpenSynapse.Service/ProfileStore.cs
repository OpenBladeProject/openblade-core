using System.Text.Json;
using OpenSynapse.Contracts;

namespace OpenSynapse.Service;

public sealed record ProfileLoadResult(ProfileSet Profiles, bool RecoveredFromInvalidFile);

public sealed class ProfileStore(string path)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task SaveAsync(ProfileSet profiles, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(profiles, SerializerOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public async Task<ProfileLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ProfileLoadResult(ProfileDefaults.CreateSafeDefaults(), false);
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            ProfileSet? profiles = JsonSerializer.Deserialize<ProfileSet>(json, SerializerOptions);
            return profiles is null
                ? new ProfileLoadResult(ProfileDefaults.CreateSafeDefaults(), true)
                : new ProfileLoadResult(profiles, false);
        }
        catch (JsonException)
        {
            return new ProfileLoadResult(ProfileDefaults.CreateSafeDefaults(), true);
        }
    }
}
