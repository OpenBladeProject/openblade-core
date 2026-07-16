using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record ProfileLoadResult(ProfileSet Profiles, bool RecoveredFromInvalidFile);

public sealed class ProfileStore(string path, ProfileSetValidator? validator = null)
{
    internal const int CurrentSchemaVersion = 1;

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private readonly ProfileSetValidator validator =
        validator ?? ProfileSetValidator.CreateUnverifiedSafeDefaults();

    internal string Path => path;

    public async Task SaveAsync(ProfileSet profiles, CancellationToken cancellationToken)
    {
        validator.Validate(profiles);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(CurrentSchemaVersion, profiles),
            SerializerOptions);
        await AtomicFile.WriteAsync(path, json, cancellationToken);
    }

    public async Task<ProfileLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ProfileLoadResult(ProfileDefaults.CreateSafeDefaults(), false);
        }

        try
        {
            byte[] json = await File.ReadAllBytesAsync(path, cancellationToken);
            ProfileSet profiles = ReadDocument(json);
            return new ProfileLoadResult(profiles, false);
        }
        catch (Exception exception) when (BatteryConfigurationStore.IsRecoverable(exception))
        {
            return await RecoverSafeDefaultsAsync(cancellationToken);
        }
    }

    internal ProfileSet ReadDocument(ReadOnlySpan<byte> json)
    {
        BatteryConfigurationStore.RejectDuplicateMembers(json);
        ProfileDocument? document = JsonSerializer.Deserialize<ProfileDocument>(
            json,
            SerializerOptions);
        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The profile schema version is unsupported.");
        }

        validator.Validate(document.Profiles);
        return document.Profiles;
    }

    private async Task<ProfileLoadResult> RecoverSafeDefaultsAsync(
        CancellationToken cancellationToken)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        await SaveAsync(defaults, cancellationToken);
        return new ProfileLoadResult(defaults, true);
    }
}

internal static class AtomicFile
{
    public static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(contents, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
