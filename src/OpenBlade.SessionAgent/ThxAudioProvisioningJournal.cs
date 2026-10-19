using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class ThxAudioProvisioningJournal : IThxAudioProvisioningJournal
{
    private const int MaximumLength = 4 * 1024 * 1024;
    private readonly FileStream stream;
    private bool hasIntent;

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenBlade.ThxAudioProvisioning");
    internal static string JournalPath => Path.Combine(Root, "installation.jsonl");

    public bool RequiresRecovery { get; private set; }
    public ThxAudioProvisioningRecovery? Recovery { get; private set; }
    public string PackageDestination { get; }

    internal ThxAudioProvisioningJournal()
    {
        EnsureProtectedDirectory(Root);
        RejectReparse(JournalPath);
        stream = new FileStream(JournalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        try
        {
            JournalState state = ReadState(stream);
            RequiresRecovery = state.Pending;
            Recovery = state.Recovery;
            stream.Position = stream.Length;
            PackageDestination = Recovery is null
                ? Path.Combine(Root, Guid.NewGuid().ToString("N"))
                : ValidatePackageDestination(Recovery.PackageDestination);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static bool HasUnfinishedTransaction()
    {
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
        => ReadState(file).Pending;

    internal static ThxAudioProvisioningRecovery? ReadRecovery(Stream file)
        => ReadState(file).Recovery;

    private static JournalState ReadState(Stream file)
    {
        try
        {
            return ReadValidState(file);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A malformed or interrupted durable record cannot prove that no
            // submission occurred. Keep it pending and prohibit continuation.
            return new(true, null);
        }
    }

    private static JournalState ReadValidState(Stream file)
    {
        if (file.Length > MaximumLength)
        {
            throw new InvalidDataException("THX provisioning journal is too large.");
        }
        using var reader = new StreamReader(file, Encoding.UTF8, true, 4096, leaveOpen: true);
        bool pending = false;
        string? packageDestination = null;
        string? profile = null;
        string? resultBootIdentity = null;
        int? resultExitCode = null;
        ThxAudioProvisioningStep? intentStep = null;
        ThxAudioProvisioningStep? lastCompletedStep = null;
        while (reader.ReadLine() is { } line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            string? kind = document.RootElement.GetProperty("kind").GetString();
            if (kind == "baseline")
            {
                pending = false;
                packageDestination = document.RootElement.TryGetProperty("packageDestination", out JsonElement destination)
                    ? destination.GetString() : null;
                profile = document.RootElement.TryGetProperty("baseline", out JsonElement baseline)
                    && baseline.TryGetProperty("Profile", out JsonElement profileElement)
                        ? profileElement.GetString() : null;
                resultBootIdentity = null;
                resultExitCode = null;
                intentStep = null;
                lastCompletedStep = null;
            }
            else if (kind == "intent")
            {
                pending = true;
                intentStep = document.RootElement.TryGetProperty("step", out JsonElement stepElement)
                    && stepElement.TryGetInt32(out int stepValue)
                    && Enum.IsDefined(typeof(ThxAudioProvisioningStep), stepValue)
                        ? (ThxAudioProvisioningStep)stepValue : null;
                resultBootIdentity = null;
                resultExitCode = null;
            }
            else if (kind == "native-result")
            {
                resultExitCode = document.RootElement.TryGetProperty("result", out JsonElement result)
                    && result.TryGetProperty("ExitCode", out JsonElement exitCode)
                        ? exitCode.GetInt32() : null;
                resultBootIdentity = document.RootElement.TryGetProperty("bootIdentity", out JsonElement boot)
                    ? boot.GetString() : null;
            }
            else if (kind == "observed")
            {
                int stepValue = document.RootElement.GetProperty("step").GetInt32();
                lastCompletedStep = Enum.IsDefined(typeof(ThxAudioProvisioningStep), stepValue)
                    ? (ThxAudioProvisioningStep)stepValue : null;
            }
            else if (kind == "complete")
            {
                pending = false;
                packageDestination = null;
                profile = null;
                resultBootIdentity = null;
                resultExitCode = null;
                intentStep = null;
                lastCompletedStep = null;
            }
        }
        ThxAudioProvisioningRecovery? recovery = pending
            && resultExitCode == (int)ThxAudioProvisioningResult.RestartRequired
            && packageDestination is not null && profile is not null
            && resultBootIdentity is not null && intentStep is not null
            // A restart observation may be durable before the next process intent.
            // Retain that checkpoint; continuation still skips the submitted step
            // and independently rechecks it on a later Windows boot.
            && (IsNextStep(lastCompletedStep, intentStep.Value) || lastCompletedStep == intentStep)
                ? new(packageDestination, profile, resultBootIdentity, intentStep.Value, lastCompletedStep)
                : null;
        return new(pending, recovery);
    }

    public void Begin(ThxAudioProvisioningSnapshot baseline, string bootIdentity) =>
        Append(new
        {
            kind = "baseline",
            at = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            processPath = Environment.ProcessPath,
            packageDestination = PackageDestination,
            bootIdentity,
            baseline,
        });

    public void Intent(ThxAudioProvisioningStep step)
    {
        ThxAudioProvisioningArtifact artifact = ThxAudioProvisioningPackage.Artifacts.Single(
            item => item.Step == step);
        Append(new
        {
            kind = "intent",
            at = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            step,
            artifact.FileName,
            source = artifact.Source.AbsoluteUri,
            artifact.Length,
            artifact.Sha256,
            artifact.Arguments,
        });
        hasIntent = true;
        RequiresRecovery = true;
    }

    public void RecordResult(ThxAudioProvisioningProcessResult result, string bootIdentity)
    {
        Append(new
        {
            kind = "native-result",
            at = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            bootIdentity,
            result,
        });
    }

    public void Observe(ThxAudioProvisioningStep step, ThxAudioProvisioningSnapshot snapshot) =>
        Append(new { kind = "observed", at = DateTimeOffset.UtcNow, step, snapshot });

    public void Complete()
    {
        Append(new { kind = "complete", at = DateTimeOffset.UtcNow, hadMutation = hasIntent });
        RequiresRecovery = false;
        Recovery = null;
    }

    private void Append(object value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
        if (stream.Length + bytes.Length > MaximumLength)
        {
            throw new IOException("THX provisioning journal is full.");
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
            foreach (WellKnownSidType sid in new[]
            {
                WellKnownSidType.BuiltinAdministratorsSid,
                WellKnownSidType.LocalSystemSid,
            })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            new DirectoryInfo(path).Create(security);
        }
        ValidateProtectedDirectory(path);
    }

    internal static void ValidateProtectedDirectory(string path)
    {
        RejectReparse(path);
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!administrators.Equals(owner) && !system.Equals(owner))
        {
            throw new UnauthorizedAccessException("Untrusted THX provisioning-directory owner.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && !administrators.Equals(rule.IdentityReference)
                && !system.Equals(rule.IdentityReference))
            {
                throw new UnauthorizedAccessException("THX provisioning directory allows non-administrator access.");
            }
        }
    }

    internal static void RejectReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("THX provisioning paths cannot contain links or reparse points.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string ValidatePackageDestination(string value)
    {
        string root = Path.GetFullPath(Root);
        string destination = Path.GetFullPath(value);
        if (!string.Equals(Path.GetDirectoryName(destination), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(destination), "N", out _))
        {
            throw new InvalidDataException("THX provisioning journal has an invalid package destination.");
        }
        ValidateProtectedDirectory(destination);
        return destination;
    }

    private static bool IsNextStep(ThxAudioProvisioningStep? completed,
        ThxAudioProvisioningStep submitted) => completed switch
        {
            null => submitted == ThxAudioProvisioningStep.InstallDriverStack,
            ThxAudioProvisioningStep.InstallDriverStack =>
                submitted == ThxAudioProvisioningStep.InstallProperties,
            ThxAudioProvisioningStep.InstallProperties =>
                submitted == ThxAudioProvisioningStep.InstallSurroundConfiguration,
            _ => false,
        };

    private sealed record JournalState(bool Pending, ThxAudioProvisioningRecovery? Recovery);

    public void Dispose() => stream.Dispose();
}
