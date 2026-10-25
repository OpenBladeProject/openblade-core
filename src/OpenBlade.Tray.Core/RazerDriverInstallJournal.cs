using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Tray;

internal sealed class RazerDriverInstallJournal : IRazerDriverInstallJournal
{
    private readonly FileStream stream;
    private readonly List<RazerDriverInventoryEntry> baseline = [];
    private bool hasIntent;
    private string? recipeId;
    public bool RequiresRecovery { get; private set; }
    public string PackageDestination { get; }

    internal static string Root => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.CommonApplicationData), "OpenBlade.DriverInstallation");
    internal static string JournalPath => Path.Combine(Root, "installation.jsonl");

    internal RazerDriverInstallJournal()
    {
        EnsureProtectedDirectory(Root);
        RejectReparse(JournalPath);
        stream = new FileStream(JournalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        if (stream.Length > 4 * 1024 * 1024)
        {
            stream.Dispose();
            throw new InvalidDataException("Driver installation journal exceeds its size bound.");
        }
        try
        {
            RequiresRecovery = HasUnfinishedTransaction(stream);
            stream.Position = stream.Length;
            PackageDestination = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            // Creation is deferred until acquisition, so an existing installation needs no package directory.
        }
        catch { stream.Dispose(); throw; }
    }

    internal static bool HasUnfinishedTransaction()
    {
        // File.Exists hides access failures. Only definitive absence can mean no prior transaction.
        try { _ = File.GetAttributes(JournalPath); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        ValidateProtectedDirectory(Root);
        RejectReparse(JournalPath);
        using var file = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { return HasUnfinishedTransaction(file); }
        catch (JsonException) { return true; }
    }

    internal static bool HasUnfinishedTransaction(Stream file)
    {
        if (file.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Driver journal is too large.");
        }

        using var reader = new StreamReader(file, Encoding.UTF8, true, 4096, leaveOpen: true);
        bool pending = false;
        while (reader.ReadLine() is { } line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            string? kind = document.RootElement.GetProperty("kind").GetString();
            if (kind == "intent")
            {
                pending = true;
            }
            else if (kind == "complete")
            {
                pending = false;
            }
        }
        return pending;
    }

    public void Begin(RazerDriverInstallSnapshot snapshot)
    {
        recipeId = snapshot.RecipeId;
        baseline.AddRange(snapshot.Inventory);
        Append(new { kind = "baseline", recipeVersion = 1, recipeId, at = DateTimeOffset.UtcNow, snapshot });
    }

    public void Intent(RazerDriverMutation operation)
    {
        Append(new { kind = "intent", recipeId, at = DateTimeOffset.UtcNow, operation });
        hasIntent = true;
        RequiresRecovery = true;
    }

    public void RecordResult(RazerDriverMutationResult result) =>
        Append(new { kind = "native-result", at = DateTimeOffset.UtcNow, result });

    public void Observe(RazerDriverMutationResult result, RazerDriverInstallSnapshot after)
    {
        // Names and hashes come from the Windows INF inventory, never localized PnPUtil output.
        // These are observations, not automatic rollback authority: concurrent unrelated installs
        // cannot be attributed to this transaction solely by their arrival time.
        var introduced = after.Inventory.Where(entry => !baseline.Any(previous =>
            string.Equals(previous.PublishedName, entry.PublishedName, StringComparison.OrdinalIgnoreCase))).ToArray();
        Append(new { kind = "observed", at = DateTimeOffset.UtcNow, result, after, introduced });
    }

    public void Complete()
    {
        Append(new { kind = "complete", at = DateTimeOffset.UtcNow, hadMutation = hasIntent });
        RequiresRecovery = false;
    }

    private void Append(object value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
        if (stream.Length + bytes.Length > 4 * 1024 * 1024)
        {
            throw new IOException("Driver journal is full.");
        }

        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static void EnsureProtectedDirectory(string path)
    {
        RejectReparse(Path.GetDirectoryName(path)!);
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(path).Create(security);
        }
        ValidateProtectedDirectory(path);
    }

    internal static void ValidateProtectedDirectory(string path)
    {
        RejectReparse(path);
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!admin.Equals(owner) && !system.Equals(owner))
        {
            throw new UnauthorizedAccessException("Untrusted installation-directory owner.");
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !admin.Equals(rule.IdentityReference)
                && !system.Equals(rule.IdentityReference))
            {
                throw new UnauthorizedAccessException("Installation directory allows non-administrator access.");
            }
        }
    }

    internal static void RejectReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Reparse points are not allowed in driver installation paths.");
            }
        }
    }

    public void Dispose() => stream.Dispose();
}
