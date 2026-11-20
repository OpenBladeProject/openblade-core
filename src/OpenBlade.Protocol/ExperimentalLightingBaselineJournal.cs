using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record ExperimentalLightingBaselineJournalLoadResult(
    ExperimentalLightingBaseline? Baseline, bool Invalid);

/// <summary>Write-ahead provenance, never an instruction to replay lighting on startup.</summary>
internal sealed class ExperimentalLightingBaselineJournal
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string directory;
    private readonly string path;
    private readonly string modelNumber;
    private readonly ushort productId;
    private readonly Action<string>? storageVerifier;
    private readonly object gate;
    private byte[]? expected;
    private bool loaded;
    private bool invalid;
    private bool capturedHere;
    private ExperimentalLightingBaseline? pending;

    internal ExperimentalLightingBaselineJournal(ExperimentalBladeModel model)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade", "ExperimentalLighting"), model.ModelNumber, model.ProductId)
    { }

    // The override is only for isolated filesystem tests; production always verifies machine-data ACLs.
    internal ExperimentalLightingBaselineJournal(string directory, string modelNumber, ushort productId,
        Action<string>? storageVerifier = null)
    {
        if (string.IsNullOrWhiteSpace(modelNumber)
            || modelNumber.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        { throw new ArgumentException("An exact model number is required.", nameof(modelNumber)); }
        this.directory = Path.GetFullPath(directory);
        this.modelNumber = modelNumber;
        this.productId = productId;
        this.storageVerifier = storageVerifier;
        path = Path.Combine(this.directory, $"{modelNumber}-1532-{productId:X4}.json");
        gate = Gates.GetOrAdd(path, _ => new object());
    }

    internal ExperimentalLightingBaselineJournalLoadResult Load()
    {
        lock (gate)
        {
            try
            {
                byte[]? bytes = Read();
                // A disappearing pending record must not become a new, clean baseline.
                if (loaded && !BytesEqual(expected, bytes))
                { throw new InvalidDataException("Lighting baseline journal changed unexpectedly."); }
                Document? document = bytes is null ? null : Decode(bytes);
                expected = bytes;
                pending = document?.Baseline;
                loaded = true;
                return new(pending, invalid);
            }
            catch
            {
                invalid = true;
                loaded = true;
                return new(pending, true);
            }
        }
    }

    // Call while holding the shared lighting operation gate, before any possible brightness/frame write.
    internal void SavePending(ExperimentalLightingBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ValidateBaseline(baseline);
        lock (gate)
        {
            EnsureUnchanged();
            if (pending is not null)
            {
                if (!capturedHere || pending != baseline)
                { throw new InvalidDataException("The original lighting baseline requires explicit built-in recovery."); }
                return;
            }
            Publish(new(SchemaFor(baseline), modelNumber, 0x1532, productId, true, baseline));
            pending = baseline;
            capturedHere = true;
        }
    }

    // Require a confirmed built-in destination, or a confirmed baseline with a known pre-submission failure.
    // A durable cleared tombstone avoids deletion/recreation races; failure leaves recovery pending.
    internal void ClearAfterVerifiedRestoration()
    {
        lock (gate)
        {
            EnsureUnchanged();
            Publish(new(1, modelNumber, 0x1532, productId, false, null));
            pending = null;
            capturedHere = false;
        }
    }

    private void EnsureUnchanged()
    {
        if (!loaded) { Load(); }
        if (invalid || !BytesEqual(expected, Read()))
        {
            invalid = true;
            throw new InvalidDataException("Lighting baseline journal requires read-only reconciliation.");
        }
    }

    private byte[]? Read()
    {
        if (!PrepareDirectory(create: false)) { return null; }
        try
        {
            VerifyFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 16384)
            { throw new InvalidDataException("Lighting baseline journal has an invalid size."); }
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    private Document Decode(byte[] bytes)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        RejectDuplicateMembers(json.RootElement);
        Document document = JsonSerializer.Deserialize<Document>(bytes, Options)
            ?? throw new InvalidDataException("Lighting baseline journal is empty.");
        // Schema 1 is the keyboard-only record. Schema 2 adds exactly one
        // restorable RZ09-0529 Vapor Chamber record; neither admits the other's shape.
        if (document.SchemaVersion is not (1 or 2) || document.ModelNumber != modelNumber
            || document.VendorId != 0x1532 || document.ProductId != productId
            || document.Pending != (document.Baseline is not null)
            || document.SchemaVersion != SchemaFor(document.Baseline))
        { throw new InvalidDataException("Lighting baseline journal identity or schema is invalid."); }
        if (document.Baseline is { } baseline) { ValidateBaseline(baseline); }
        return document;
    }

    private static int SchemaFor(ExperimentalLightingBaseline? baseline) =>
        baseline?.VaporChamber is null ? 1 : 2;

    private static void ValidateBaseline(ExperimentalLightingBaseline baseline)
    {
        LightingSettings settings = baseline.Settings;
        // No reviewed LED38 setter changes record brightness, so its saved
        // destination is always the nominal full-brightness setting.
        if (!IsCompleteBuiltIn(settings)
            || settings.Effect != LightingEffect.Off
                && settings.Brightness != LightingBrightnessProtocol.RawToPercent(baseline.RawBrightness)
            || baseline.VaporChamber is { } vaporChamber
                && (!IsCompleteBuiltIn(vaporChamber)
                    || vaporChamber.Effect != LightingEffect.Off && vaporChamber.Brightness != 100))
        { throw new InvalidDataException("Lighting baseline is not a complete built-in setting."); }
    }

    private static bool IsCompleteBuiltIn(LightingSettings settings) =>
        LightingSettingsValidation.IsWellFormed(settings)
        && settings.Effect is (LightingEffect.Off or LightingEffect.Static
            or LightingEffect.Breathing or LightingEffect.Wave)
        && settings.Parameters is not null && settings.LogoMode is null
        && settings.IndependentControls is null && !settings.ControlsKeyboardEffect;

    private void Publish(Document document)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PrepareDirectory(create: true);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            VerifyFile(temporary);
            // Recheck after durable staging, before atomically publishing ownership provenance.
            EnsureUnchanged();
            File.Move(temporary, path, overwrite: true);
            expected = bytes;
        }
        catch
        {
            invalid = true;
            throw;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private bool PrepareDirectory(bool create)
    {
        if (storageVerifier is not null)
        {
            storageVerifier(directory);
            if (create) { Directory.CreateDirectory(directory); }
            return Directory.Exists(directory);
        }
        string parent = Path.GetDirectoryName(directory)!;
        // Setup owns root creation and protection. Discovery neither creates nor repairs it.
        try { VerifyProtected(new DirectoryInfo(parent)); }
        catch (DirectoryNotFoundException) when (!create) { return false; }
        catch (FileNotFoundException) when (!create) { return false; }
        try { File.GetAttributes(directory); }
        catch (DirectoryNotFoundException) { if (!create) { return false; } Directory.CreateDirectory(directory); }
        catch (FileNotFoundException) { if (!create) { return false; } Directory.CreateDirectory(directory); }
        VerifyProtected(new DirectoryInfo(directory));
        return true;
    }

    private void VerifyFile(string file)
    {
        if (storageVerifier is null) { VerifyProtected(new FileInfo(file)); }
        else if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
        { throw new UnauthorizedAccessException("Lighting baseline cannot be a reparse point."); }
    }

    private static void VerifyProtected(FileSystemInfo info)
    {
        // Same protected machine-data policy as CoolingPadStateStore; no dependency on the service layer.
        for (string? current = info.FullName; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            { throw new UnauthorizedAccessException("Lighting storage must not contain reparse points."); }
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
            || !rules.Any(rule => rule.IdentityReference == system)
            || !rules.Any(rule => rule.IdentityReference == administrators)
            || rules.Any(rule => !Trusted(rule.IdentityReference) || rule.AccessControlType != AccessControlType.Allow
                || rule.FileSystemRights != FileSystemRights.FullControl))
        { throw new UnauthorizedAccessException("Lighting storage must be protected for SYSTEM and Administrators."); }
    }

    private static bool BytesEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate lighting journal member."); }
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (JsonElement child in element.EnumerateArray()) { RejectDuplicateMembers(child); } }
    }

    private sealed record Document(int SchemaVersion, string ModelNumber, ushort VendorId,
        ushort ProductId, bool Pending, ExperimentalLightingBaseline? Baseline);
}
