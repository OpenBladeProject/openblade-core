using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record ProfileLoadResult(ProfileSet Profiles, bool RecoveredFromInvalidFile);

public sealed class ProfileStore(string path, ProfileSetValidator? validator = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly ProfileSetValidator _validator =
        validator ?? ProfileSetValidator.CreateUnverifiedSafeDefaults();

    public async Task SaveAsync(ProfileSet profiles, CancellationToken cancellationToken)
    {
        _validator.Validate(profiles);
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
            if (profiles is null)
            {
                return await RecoverSafeDefaultsAsync(cancellationToken);
            }

            _validator.Validate(profiles);
            return new ProfileLoadResult(profiles, false);
        }
        catch (Exception exception) when (exception is
            JsonException or
            NotSupportedException or
            ArgumentException or
            InvalidDataException)
        {
            return await RecoverSafeDefaultsAsync(cancellationToken);
        }
    }

    private async Task<ProfileLoadResult> RecoverSafeDefaultsAsync(
        CancellationToken cancellationToken)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        await SaveAsync(defaults, cancellationToken);
        return new ProfileLoadResult(defaults, true);
    }
}
