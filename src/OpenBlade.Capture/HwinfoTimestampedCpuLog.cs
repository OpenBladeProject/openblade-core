using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

internal sealed record HwinfoProcessIdentity(int ProcessId, DateTimeOffset StartedAtUtc,
    string ExecutablePath, string Sha256, string Version, bool SignatureVerified);

internal sealed record HwinfoCpuLogObservation(DateTimeOffset? SourceTimestampUtc,
    DateTimeOffset ObservedAtUtc, double? Celsius, double? SourceAgeMilliseconds,
    string SensorIdentitySha256, string? Failure, bool IsPrimingRead,
    double? NewSamplesMaximumCelsius, DateTimeOffset? FirstNewSourceTimestampUtc,
    DateTimeOffset? LastNewSourceTimestampUtc);

/// <summary>Developer-only observation of one explicitly selected, already-running HWiNFO logger.</summary>
internal sealed class HwinfoTimestampedCpuLog : IDisposable
{
    internal const string SensorGroup = "CPU [#0]: AMD Ryzen AI 9 HX 370: Enhanced";
    internal const string SensorName = "CPU (Tctl/Tdie)";
    internal const string SensorUnit = "°C";
    internal static readonly string SensorIdentitySha256 = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(SensorGroup + "\n" + SensorName + "\n" + SensorUnit)));
    private const int MaximumPendingBytes = 512 * 1024;
    private const int MaximumReadBytesPerCall = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly FileStream stream;
    private readonly string fileIdentity;
    private readonly HwinfoProcessIdentity expectedProcess;
    private readonly Func<HwinfoProcessIdentity?> observeProcess;
    private readonly TimeProvider time;
    private readonly TimeZoneInfo zone;
    private readonly DateTimeOffset initialUtc;
    private readonly long initialTicks;
    private readonly List<byte> pending = [];
    private bool rootOpened;
    private bool needComma;
    private bool afterComma;
    private bool rootClosed;
    private bool disposed;
    private string? failure;
    private DateTimeOffset? latest;
    private double? celsius;
    private long advancedAt;
    private long previousLength;
    private bool primed;
    private double? newMaximum;
    private DateTimeOffset? firstNewTimestamp;
    private DateTimeOffset? lastNewTimestamp;

    internal HwinfoTimestampedCpuLog(string path, HwinfoProcessIdentity expectedProcess,
        Func<HwinfoProcessIdentity?> observeProcess, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(expectedProcess.ExecutablePath)
            || expectedProcess.ProcessId <= 0 || !expectedProcess.SignatureVerified
            || expectedProcess.Sha256.Length != 64 || !expectedProcess.Sha256.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(expectedProcess.Version)
            || !Path.GetFileName(expectedProcess.ExecutablePath).Equals("HWiNFO64.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Explicit log and verified HWiNFO process identity required.");
        }

        this.expectedProcess = expectedProcess with { ExecutablePath = Path.GetFullPath(expectedProcess.ExecutablePath) };
        this.observeProcess = observeProcess;
        this.time = time ?? TimeProvider.System;
        this.zone = zone ?? TimeZoneInfo.Local;
        initialUtc = this.time.GetUtcNow();
        initialTicks = this.time.GetTimestamp();
        RequireProcess();
        // Denying delete sharing pins the pathname to this same open file for the reader lifetime.
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { fileIdentity = GetIdentity(stream.SafeFileHandle); }
        catch { stream.Dispose(); throw; }
    }

    internal HwinfoCpuLogObservation ReadFresh(CancellationToken cancellationToken = default)
    {
        bool isPrimingRead = !primed;
        newMaximum = null;
        firstNewTimestamp = null;
        lastNewTimestamp = null;
        if (failure is null)
        {
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                long readStarted = time.GetTimestamp();
                RequireProcess();
                if (GetIdentity(stream.SafeFileHandle) != fileIdentity) { throw new InvalidDataException("FileIdentityChanged"); }
                long length = stream.Length;
                if (length < previousLength || length < stream.Position) { throw new InvalidDataException("FileTruncated"); }
                previousLength = length;
                long bytesToRead = length - stream.Position;
                if (bytesToRead > MaximumReadBytesPerCall) { throw new InvalidDataException("ReadBudgetExceeded"); }
                byte[] buffer = new byte[16384];
                while (bytesToRead > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (time.GetElapsedTime(readStarted).TotalMilliseconds > 2000)
                    {
                        throw new InvalidDataException("ReadTimeBudgetExceeded");
                    }
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytesToRead));
                    if (count == 0) { throw new IOException("UnexpectedReadEnd"); }
                    pending.AddRange(buffer.AsSpan(0, count).ToArray());
                    bytesToRead -= count;
                    ConsumeCompleteMembers();
                    if (pending.Count > MaximumPendingBytes) { throw new InvalidDataException("IncompleteObjectTooLarge"); }
                }

                cancellationToken.ThrowIfCancellationRequested();
                RequireProcess();
                cancellationToken.ThrowIfCancellationRequested();
                if (time.GetElapsedTime(readStarted).TotalMilliseconds > 2000)
                {
                    throw new InvalidDataException("ReadTimeBudgetExceeded");
                }
                DateTimeOffset now = time.GetUtcNow();
                double elapsed = time.GetElapsedTime(initialTicks).TotalMilliseconds;
                if (Math.Abs((now - initialUtc).TotalMilliseconds - elapsed) > 500) { throw new InvalidDataException("WallClockDiscontinuity"); }
                if (latest is null) { throw new InvalidDataException("NoCompleteTimestampObject"); }
                double age = (now - latest.Value).TotalMilliseconds;
                if (age < 0 || age > 4000 || time.GetElapsedTime(advancedAt).TotalMilliseconds > 4000)
                {
                    throw new InvalidDataException("StaleOrFutureTimestamp");
                }
                primed = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // No recovery from a source fault within an armed experiment; callers must re-arm.
                failure = error is OperationCanceledException ? "Canceled" : error.GetType().Name + ":" + SafeReason(error);
            }
        }

        DateTimeOffset observed = time.GetUtcNow();
        return new(latest, observed, celsius, latest is null ? null : (observed - latest.Value).TotalMilliseconds,
            SensorIdentitySha256, failure, isPrimingRead,
            isPrimingRead ? null : newMaximum, isPrimingRead ? null : firstNewTimestamp,
            isPrimingRead ? null : lastNewTimestamp);
    }

    private static string SafeReason(Exception error) => error is InvalidDataException
        ? error.Message : "SourceObservationFailed";

    private void RequireProcess()
    {
        HwinfoProcessIdentity? actual = observeProcess();
        if (actual is null || actual.ProcessId != expectedProcess.ProcessId
            || actual.StartedAtUtc != expectedProcess.StartedAtUtc || !actual.SignatureVerified
            || !Path.GetFullPath(actual.ExecutablePath).Equals(expectedProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !actual.Sha256.Equals(expectedProcess.Sha256, StringComparison.OrdinalIgnoreCase)
            || actual.Version != expectedProcess.Version)
        {
            throw new InvalidDataException("HwinfoProcessIdentityChanged");
        }
    }

    private void ConsumeCompleteMembers()
    {
        while (true)
        {
            int leading = 0;
            while (leading < pending.Count && IsWhite(pending[leading])) { leading++; }
            if (leading > 0) { pending.RemoveRange(0, leading); }
            if (pending.Count == 0) { return; }
            if (rootClosed) { throw new InvalidDataException("ContentAfterRoot"); }
            if (!rootOpened)
            {
                if (pending[0] != '{') { throw new InvalidDataException("ExpectedTimestampRoot"); }
                pending.RemoveAt(0); rootOpened = true; continue;
            }
            if (pending[0] == '}')
            {
                if (afterComma) { throw new InvalidDataException("TrailingComma"); }
                pending.RemoveAt(0); rootClosed = true; continue;
            }
            if (needComma)
            {
                if (pending[0] != ',') { throw new InvalidDataException("MissingMemberSeparator"); }
                pending.RemoveAt(0); needComma = false; afterComma = true; continue;
            }
            if (pending[0] != '"') { throw new InvalidDataException("ExpectedTimestampProperty"); }
            int keyEnd = -1;
            bool keyEscape = false;
            for (int i = 1; i < pending.Count; i++)
            {
                if (keyEscape) { keyEscape = false; continue; }
                if (pending[i] == '\\') { keyEscape = true; continue; }
                if (pending[i] == '"') { keyEnd = i; break; }
            }
            if (keyEnd < 0) { return; }
            int valueStart = keyEnd + 1;
            while (valueStart < pending.Count && IsWhite(pending[valueStart])) { valueStart++; }
            if (valueStart == pending.Count) { return; }
            if (pending[valueStart++] != ':') { throw new InvalidDataException("ExpectedTimestampColon"); }
            while (valueStart < pending.Count && IsWhite(pending[valueStart])) { valueStart++; }
            if (valueStart == pending.Count) { return; }
            if (pending[valueStart] != '{') { throw new InvalidDataException("ExpectedTimestampObject"); }
            bool quoted = false, escaped = false, valueStarted = false;
            int depth = 0, end = -1;
            for (int i = 0; i < pending.Count; i++)
            {
                byte current = pending[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; continue; }
                    if (current == '\\') { escaped = true; continue; }
                    if (current == '"') { quoted = false; }
                    continue;
                }
                if (current == '"') { quoted = true; }
                else if (current == '{') { depth++; valueStarted = true; }
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0 && valueStarted) { end = i; break; }
                    if (depth < 0) { throw new InvalidDataException("InvalidMemberDepth"); }
                }
            }
            if (end < 0) { return; }
            if (end + 1 > MaximumPendingBytes) { throw new InvalidDataException("TimestampObjectTooLarge"); }
            string member = StrictUtf8.GetString(pending.GetRange(0, end + 1).ToArray());
            using JsonDocument document = JsonDocument.Parse("{" + member + "}", new JsonDocumentOptions { MaxDepth = 8 });
            RequireUniqueProperties(document.RootElement);
            JsonProperty timestamp = document.RootElement.EnumerateObject().Single();
            DateTime local = DateTime.ParseExact(timestamp.Name.Trim(), "d.M.yyyy H:m:s.fff", CultureInfo.InvariantCulture);
            if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local)) { throw new InvalidDataException("AmbiguousLocalTimestamp"); }
            DateTimeOffset source = new(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            if (source < expectedProcess.StartedAtUtc)
            {
                throw new InvalidDataException("LogPredatesVerifiedProcess");
            }
            if (latest is not null && source <= latest.Value) { throw new InvalidDataException("NonAdvancingTimestamp"); }
            JsonElement selected = timestamp.Value.GetProperty(SensorGroup).GetProperty(SensorName);
            if (selected.GetProperty("Unit").GetString() != SensorUnit) { throw new InvalidDataException("SensorUnitChanged"); }
            double value = double.Parse(selected.GetProperty("Value").GetString()!, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value) || value <= 0) { throw new InvalidDataException("InvalidCpuTemperature"); }
            latest = source; celsius = value; advancedAt = time.GetTimestamp();
            newMaximum = Math.Max(newMaximum ?? value, value);
            firstNewTimestamp ??= source;
            lastNewTimestamp = source;
            pending.RemoveRange(0, end + 1); needComma = true; afterComma = false;
        }
    }

    private static bool IsWhite(byte value) => value is 32 or 9 or 10 or 13;

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { throw new InvalidDataException("UnexpectedArray"); }
        if (element.ValueKind != JsonValueKind.Object) { return; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) { throw new InvalidDataException("DuplicateProperty"); }
            RequireUniqueProperties(property.Value);
        }
    }

    public void Dispose()
    {
        disposed = true;
        stream.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    private static string GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation value)) { throw new IOException("FileIdentityUnavailable"); }
        return $"{value.Volume:X8}:{value.IndexHigh:X8}:{value.IndexLow:X8}";
    }
}
