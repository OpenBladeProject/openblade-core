using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Capture;

// Existing records identify the prior attempt. They do not confer authority
// or tell us which actor owns the live mailbox fields.
internal sealed class So6120MailboxObservationEvidence : IDisposable
{
    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 32, AllowDuplicateProperties = false };
    private readonly List<FileStream> locked = [];

    private So6120MailboxObservationEvidence() { }

    internal So6120JournalIdentity Identity { get; private set; } = null!;
    internal string JournalSha256 { get; private set; } = "";
    internal string AttendedLogSha256 { get; private set; } = "";
    internal string ParentReceiptSha256 { get; private set; } = "";

    internal static So6120MailboxObservationEvidence Open(string priorRoot)
    {
        priorRoot = So6120DirectDutyJournal.ValidateRunRoot(priorRoot);
        string parent = So6120DirectDutyJournal.ValidateRunRoot(Path.GetDirectoryName(priorRoot)!);
        var prior = new So6120MailboxObservationEvidence();
        try
        {
            var journal = prior.Lock(Path.Combine(priorRoot, So6120DirectDutyJournal.FileName),
                So6120DirectDutyJournal.MaximumFileBytes);
            var inspection = So6120DirectDutyJournal.Inspect(priorRoot);
            if (inspection.FormatVersion != 2 || !inspection.Fenced || inspection.Complete || inspection.Corrupt
                || inspection.PermissionPublicationPossible || inspection.Identity is not { } identity
                || identity.FirmwareEvidenceSha256 != So6120SilentPwmCommand.FirmwareHash)
            { throw new InvalidDataException("An intact fenced v2 journal without possible duty permission is required."); }
            prior.Identity = identity;
            prior.JournalSha256 = Hash(journal);

            var log = prior.Lock(Path.Combine(priorRoot, "attended-run.jsonl"), 1024 * 1024);
            prior.AttendedLogSha256 = Hash(log);
            VerifyAttendedLog(log, identity);

            var receipt = prior.Lock(Path.Combine(parent, "parent-result.json"), 16 * 1024);
            prior.ParentReceiptSha256 = Hash(receipt);
            using var document = JsonDocument.Parse(receipt, Json);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || !root.GetProperty("observedChildExit").GetBoolean()
                || root.GetProperty("childPid").GetInt32() <= 0
                || root.GetProperty("exitCode").GetInt32() != 2
                || !string.Equals(root.GetProperty("attemptRoot").GetString(), priorRoot, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("The parent receipt must record this attempt's failed child exit."); }
            return prior;
        }
        catch { prior.Dispose(); throw; }
    }

    internal void LockAttemptMarker(string directory)
    {
        directory = So6120DirectDutyJournal.ValidateRunRoot(directory);
        var marker = Lock(Path.Combine(directory, "attempt.json"), 4096);
        using var document = JsonDocument.Parse(marker, Json);
        if (document.RootElement.GetProperty("runId").GetGuid() != Identity.Owner.OperationId)
        { throw new InvalidDataException("The machine attempt marker does not name this prior run."); }
    }

    internal string LockPinnedEvidence(string path, long maximumBytes, string expectedSha256)
    {
        string hash = Hash(Lock(path, maximumBytes));
        if (hash != expectedSha256)
        { throw new InvalidDataException($"The reviewed evidence hash differs for {Path.GetFileName(path)}."); }
        return hash;
    }

    private static void VerifyAttendedLog(FileStream log, So6120JournalIdentity identity)
    {
        using var reader = new StreamReader(log, new UTF8Encoding(false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        bool closed = false;
        int rows = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++rows > 256 || closed)
            { throw new InvalidDataException("The prior log must end with one terminal close record."); }
            using var document = JsonDocument.Parse(line, Json);
            var row = document.RootElement;
            string? phase = row.GetProperty("phase").GetString();
            if (rows == 1 && (phase != "created" || row.GetProperty("run").GetGuid() != identity.Owner.OperationId
                || row.GetProperty("FirmwareHash").GetString() != identity.FirmwareEvidenceSha256))
            { throw new InvalidDataException("The prior log does not match the journal identity."); }
            if (phase != "closed") { continue; }
            var close = row.GetProperty("close");
            if (!row.GetProperty("acquisitionStarted").GetBoolean()
                || row.GetProperty("cleanupConfirmed").GetBoolean()
                || row.GetProperty("acquisitionFailureClose").ValueKind != JsonValueKind.Null
                || row.GetProperty("releaseFailures").GetArrayLength() != 0
                || close.GetProperty("State").GetInt32() != (int)So6120NativeCloseState.Closed
                || close.GetProperty("NativeError").GetInt32() != 0)
            { throw new InvalidDataException("The prior log must establish a clean native close and incomplete cleanup."); }
            closed = true;
        }
        if (!closed) { throw new InvalidDataException("The prior log has no confirmed terminal native close."); }
    }

    private FileStream Lock(string path, long maximumBytes)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidDataException("Observation evidence may not be a reparse point."); }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        locked.Add(stream);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || stream.Length <= 0 || stream.Length > maximumBytes)
        { throw new InvalidDataException("Observation evidence is outside the reviewed size bound."); }
        return stream;
    }

    private static string Hash(FileStream stream)
    {
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    public void Dispose()
    {
        foreach (var stream in locked) { stream.Dispose(); }
    }
}
