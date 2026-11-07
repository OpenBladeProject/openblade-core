using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record CoolingPadOwnershipRecord(
    string Owner, CoolingPadDeviceSnapshot Baseline, bool FanChanged, bool LightingChanged, int SchemaVersion = 1);
internal sealed record CoolingPadSettingsDocument(int SchemaVersion, CoolingPadSettings Settings);

/// <summary>Machine-owned atomic state. An unreadable record never means that ownership is absent.</summary>
internal sealed class CoolingPadStateStore(string? directory = null, Action<string>? storageVerifier = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly Dictionary<string, CoolingPadSettings> memorySettings = new(StringComparer.Ordinal);
    private CoolingPadOwnershipRecord? memoryOwnership;

    internal async Task<CoolingPadSettings> LoadSettingsAsync(string owner, CancellationToken token)
    {
        CoolingPadSettingsDocument? document = await ReadAsync<CoolingPadSettingsDocument>(
            PathForOwner(owner), token).ConfigureAwait(false);
        CoolingPadSettings settings = document?.Settings
            ?? memorySettings.GetValueOrDefault(owner) ?? CoolingPadSettings.Default;
        if (document is not null && document.SchemaVersion != 1 || !CoolingPadSettings.IsWellFormed(settings))
        { throw new InvalidDataException("Saved cooling pad settings are unsupported or invalid."); }
        return settings;
    }

    internal async Task SaveSettingsAsync(string owner, CoolingPadSettings settings, CancellationToken token)
    {
        if (!CoolingPadSettings.IsWellFormed(settings)) { throw new InvalidDataException("Cooling pad settings are invalid."); }
        await WriteAsync(PathForOwner(owner), new CoolingPadSettingsDocument(1, settings), token).ConfigureAwait(false);
        memorySettings[owner] = settings;
    }

    internal async Task<CoolingPadOwnershipRecord?> LoadOwnershipAsync(CancellationToken token)
    {
        CoolingPadOwnershipRecord? record = directory is null ? memoryOwnership
            : await ReadAsync<CoolingPadOwnershipRecord>(OwnershipPath, token).ConfigureAwait(false);
        if (record is not null) { Validate(record); }
        return record;
    }

    internal async Task SaveOwnershipAsync(CoolingPadOwnershipRecord record, CancellationToken token)
    {
        Validate(record);
        await WriteAsync(OwnershipPath, record, token).ConfigureAwait(false);
        memoryOwnership = record;
    }

    internal Task ClearOwnershipAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (OwnershipPath is { } path && PrepareDirectory(create: false))
        {
            ValidateFileIfPresent(path);
            File.Delete(path);
        }
        memoryOwnership = null;
        return Task.CompletedTask;
    }

    private string? OwnershipPath => directory is null ? null : Path.Combine(directory, "ownership.json");
    private string? PathForOwner(string owner) => directory is null ? null : Path.Combine(directory,
        "settings-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))) + ".json");

    private void Validate(CoolingPadOwnershipRecord record)
    {
        CoolingPadDeviceSnapshot snapshot = record.Baseline;
        if (record.SchemaVersion != 1 || string.IsNullOrWhiteSpace(record.Owner) || record.Owner.Length > 184
            || snapshot is null || string.IsNullOrWhiteSpace(snapshot.Identity) || snapshot.Identity.Length > 256
            || !record.FanChanged && !record.LightingChanged
            || snapshot.FanMode is { } fanMode && !Enum.IsDefined(fanMode)
            || snapshot.LightingMode is { } lightingMode && !Enum.IsDefined(lightingMode)
            || snapshot.TargetRpm is < 0 or > 3200 || snapshot.TargetRpm is { } rpm && rpm % 50 != 0
            || snapshot.BrightnessPercent is < 0 or > 100
            || snapshot.DeviceMode is { } mode && !Enum.IsDefined(mode))
        { throw new InvalidDataException("Cooling pad restoration state is unsupported or invalid."); }
        if (directory is not null && storageVerifier is null
            && (snapshot.Identity.Length != 64 || !snapshot.Identity.All(Uri.IsHexDigit)
                || !new SecurityIdentifier(record.Owner).IsAccountSid()))
        { throw new InvalidDataException("Cooling pad restoration identity is invalid."); }
        if (record.FanChanged && snapshot.FanConfiguration is null)
        { throw new InvalidDataException("Cooling pad fan restoration state is missing."); }
        if (snapshot.FanConfiguration is { } fan && (!Enum.IsDefined(fan.Ownership)
            || !Enum.IsDefined(fan.Preset) || fan.ConfiguredRpm is < 0 or > 3200 || fan.ConfiguredRpm % 50 != 0))
        { throw new InvalidDataException("Cooling pad fan restoration state is invalid."); }
        if (snapshot.LightingSettings is { } settings && !CoolingPadSettings.IsWellFormed(settings))
        { throw new InvalidDataException("Cooling pad lighting restoration intent is invalid."); }
        if (record.LightingChanged && (snapshot.LightingConfiguration is null || snapshot.ActiveLightingConfiguration is null
            || snapshot.DeviceMode is null || snapshot.BrightnessRaw is null
            || snapshot.LightingConfiguration.Effect == 8 || snapshot.ActiveLightingConfiguration?.Effect == 8))
        { throw new InvalidDataException("The original cooling pad lighting cannot be restored exactly."); }
        ValidateLighting(snapshot.LightingConfiguration);
        ValidateLighting(snapshot.ActiveLightingConfiguration);
    }

    private static void ValidateLighting(CoolingPadLightingConfiguration? lighting)
    {
        if (lighting is not null && (lighting.Profile is not (0 or 1) || lighting.Region != 0
            || lighting.Effect is not (0 or 1 or 2 or 3 or 4 or 8) || lighting.Colors is null
            || lighting.Colors.Count > 2 || lighting.Colors.Any(color => color is null)))
        { throw new InvalidDataException("Cooling pad lighting restoration state is invalid."); }
    }

    private async Task<T?> ReadAsync<T>(string? path, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        if (path is null || !PrepareDirectory(create: false)) { return null; }
        try
        {
            ValidateFileIfPresent(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 64 * 1024) { throw new InvalidDataException("Cooling pad state has an invalid size."); }
            byte[] bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(bytes);
            RejectDuplicateMembers(document.RootElement);
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) || !schema.TryGetInt32(out int version) || version != 1)
            { throw new InvalidDataException("Cooling pad state schema is unsupported."); }
            return JsonSerializer.Deserialize<T>(bytes, Options)
                ?? throw new InvalidDataException("Cooling pad state is invalid.");
        }
        catch (FileNotFoundException) { return null; }
    }

    private async Task WriteAsync<T>(string? path, T value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path is null) { return; }
        PrepareDirectory(create: true);
        ValidateFileIfPresent(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ValidateFileIfPresent(temporary);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    private bool PrepareDirectory(bool create)
    {
        if (directory is null) { return true; }
        string fullPath = Path.GetFullPath(directory);
        if (storageVerifier is not null)
        {
            storageVerifier(fullPath);
            if (create) { Directory.CreateDirectory(fullPath); }
            return Directory.Exists(fullPath);
        }
        // Setup protects the machine-data root for SYSTEM and Administrators.
        // Reuse that policy and inherit it for this fixed child; never repair an unsafe root.
        string parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Cooling pad storage has no machine-data parent.");
        VerifyProtected(new DirectoryInfo(parent));
        try { File.GetAttributes(fullPath); }
        catch (DirectoryNotFoundException) { if (!create) { return false; } Directory.CreateDirectory(fullPath); }
        catch (FileNotFoundException) { if (!create) { return false; } Directory.CreateDirectory(fullPath); }
        VerifyProtected(new DirectoryInfo(fullPath));
        return true;
    }

    private void ValidateFileIfPresent(string path)
    {
        try
        {
            File.GetAttributes(path);
            if (storageVerifier is null) { VerifyProtected(new FileInfo(path)); }
            else if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            { throw new UnauthorizedAccessException("Cooling pad state must not be a reparse point."); }
        }
        catch (FileNotFoundException) { }
    }

    private static void VerifyProtected(FileSystemInfo info)
    {
        for (string? current = info.FullName; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            { throw new UnauthorizedAccessException("Cooling pad storage must not contain reparse points."); }
        }
        FileSystemSecurity security = info is DirectoryInfo folder
            ? folder.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : ((FileInfo)info).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        bool Trusted(IdentityReference? identity) => identity == system || identity == administrators;
        FileSystemAccessRule[] rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))) || rules.Length != 2
            || !rules.Any(rule => rule.IdentityReference == system) || !rules.Any(rule => rule.IdentityReference == administrators)
            || rules.Any(rule => !Trusted(rule.IdentityReference) || rule.AccessControlType != AccessControlType.Allow
                || rule.FileSystemRights != FileSystemRights.FullControl))
        { throw new UnauthorizedAccessException("Cooling pad state must be owned and writable only by SYSTEM and Administrators."); }
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new InvalidDataException("Cooling pad state contains duplicate members."); }
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (JsonElement child in element.EnumerateArray()) { RejectDuplicateMembers(child); } }
    }
}
