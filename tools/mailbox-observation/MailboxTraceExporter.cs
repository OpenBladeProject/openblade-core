// Offline ETL reading only. This file never starts or controls an ETW session.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace OpenBlade.MailboxObservation
{
    public static class TraceExporter
    {
        private const long MaximumInputBytes = 128L * 1024 * 1024;
        private const long MaximumOutputBytes = 512L * 1024 * 1024;
        private const int MaximumXmlCharacters = 131072;

        // Public for offline fixture tests. Event XML is never interpreted as code.
        public static Dictionary<string, object> ParseEventXml(string xml)
        {
            var settings = new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlCharacters,
            };
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);
            var document = XDocument.Load(reader);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var root = document.Root;
            if (root == null || root.Name != ns + "Event")
                throw new InvalidDataException("Expected Windows event XML");
            var system = root.Element(ns + "System") ?? throw new InvalidDataException("Missing System");
            var provider = system.Element(ns + "Provider");
            string guid = (string)provider?.Attribute("Guid");
            if (!Guid.TryParse(guid, out Guid providerGuid))
                throw new InvalidDataException("Missing provider GUID");
            string time = (string)system.Element(ns + "TimeCreated")?.Attribute("SystemTime");
            if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp))
                throw new InvalidDataException("Missing event timestamp");
            var execution = system.Element(ns + "Execution");
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            var eventData = root.Element(ns + "EventData");
            if (eventData != null)
                foreach (var field in eventData.Elements(ns + "Data"))
                {
                    string name = (string)field.Attribute("Name");
                    if (string.IsNullOrEmpty(name) || !data.TryAdd(name, field.Value))
                        throw new InvalidDataException("Unnamed or repeated EventData field");
                }
            var error = root.Element(ns + "ProcessingErrorData");
            bool unsupportedPayload = root.Element(ns + "UserData") != null;
            if (eventData != null)
                foreach (var child in eventData.Elements())
                    if (child.Name != ns + "Data") unsupportedPayload = true;
            string payload = (string)error?.Element(ns + "EventPayload");
            if (payload != null)
            {
                if (payload.Length > 131072 || (payload.Length & 1) != 0)
                    throw new InvalidDataException("Invalid payload length");
                foreach (char value in payload)
                    if (!Uri.IsHexDigit(value))
                        throw new InvalidDataException("Invalid payload hex");
            }
            return new Dictionary<string, object> {
                ["type"] = "event",
                ["provider"] = providerGuid.ToString("D"),
                ["id"] = Number((string)system.Element(ns + "EventID")),
                ["version"] = Number((string)system.Element(ns + "Version")),
                ["opcode"] = Number((string)system.Element(ns + "Opcode")),
                ["pid"] = Number((string)execution?.Attribute("ProcessID")),
                ["tid"] = Number((string)execution?.Attribute("ThreadID")),
                ["time_utc"] = timestamp.ToUniversalTime().ToString("o"),
                ["record_id"] = Number((string)system.Element(ns + "EventRecordID")),
                ["payload_hex"] = payload,
                ["event_data"] = data,
                ["render_error"] = error == null ? null : Number((string)error.Element(ns + "ErrorCode")),
                ["unsupported_payload_form"] = unsupportedPayload,
            };
        }

        private static ulong? Number(string value)
        {
            if (value == null) return null;
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result))
                throw new InvalidDataException("Invalid unsigned event field");
            return result;
        }

        public static string Export(string input, string output, int maximumEvents, int timeoutSeconds)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ETL export needs Windows");
            if (maximumEvents < 1 || maximumEvents > 2000000) throw new ArgumentOutOfRangeException(nameof(maximumEvents));
            if (timeoutSeconds < 1 || timeoutSeconds > 300) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            if (input.Replace('/', '\\').StartsWith("\\\\", StringComparison.Ordinal) ||
                output.Replace('/', '\\').StartsWith("\\\\", StringComparison.Ordinal))
                throw new InvalidDataException("UNC and device namespace paths are not supported");
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);
            var inputAttributes = File.GetAttributes(input);
            if ((inputAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException("Input must be an ordinary local file, not a link or device");
            if (!string.Equals(Path.GetExtension(input), ".etl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Input must be an existing .etl file");
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase) || File.Exists(output) || Directory.Exists(output))
                throw new IOException("Output must be new");
            string directory = Path.GetDirectoryName(output);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Output parent must exist");
            string temporary = Path.Combine(directory, ".mailbox-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var sourceLock = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (sourceLock.Length == 0 || sourceLock.Length > MaximumInputBytes)
                throw new InvalidDataException("ETL input exceeds the 128 MiB bound or is empty");
            string hash = Convert.ToHexString(SHA256.HashData(sourceLock)).ToLowerInvariant();
            var clock = Stopwatch.StartNew();
            bool ownsTemporary = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536);
                    writer.NewLine = "\n";
                    long writtenBytes = 0;
                    Write(writer, ref writtenBytes, new Dictionary<string, object> {
                        ["type"] = "header", ["schema"] = "openblade.mailbox-trace-export",
                        ["schema_version"] = 1, ["etl_sha256"] = hash,
                        ["etl_bytes"] = sourceLock.Length,
                        ["source_file_name"] = Path.GetFileName(input),
                        ["scope"] = "offline-file-only",
                    });
                    var query = new EventLogQuery(input, PathType.FilePath) {
                        ReverseDirection = false, TolerateQueryErrors = false,
                    };
                    using var events = new EventLogReader(query);
                    long count = 0;
                    var providers = new Dictionary<string, long>(StringComparer.Ordinal);
                    while (true)
                    {
                        if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
                            throw new TimeoutException("Offline export deadline reached");
                        using EventRecord record = events.ReadEvent(TimeSpan.FromSeconds(1));
                        if (record == null) break;
                        if (++count > maximumEvents) throw new InvalidDataException("Event count bound reached");
                        // Preserve raw ProcessingErrorData: unknown installed schemas are not absence.
                        var decoded = ParseEventXml(record.ToXml());
                        string provider = (string)decoded["provider"];
                        providers.TryGetValue(provider, out long previous);
                        providers[provider] = previous + 1;
                        Write(writer, ref writtenBytes, decoded);
                    }
                    Write(writer, ref writtenBytes, new Dictionary<string, object> {
                        ["type"] = "end", ["complete"] = true, ["events"] = count,
                        ["providers"] = providers, ["elapsed_seconds"] = clock.Elapsed.TotalSeconds,
                    });
                    writer.Flush();
                    if (stream.Position > MaximumOutputBytes) throw new InvalidDataException("Export byte bound reached");
                    stream.Flush(true);
                }
                File.Move(temporary, output, false);
                return output;
            }
            finally
            {
                // Only the unique temporary file created by this invocation may be removed.
                if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void Write(StreamWriter writer, ref long writtenBytes, object value)
        {
            string line = JsonSerializer.Serialize(value);
            long bytes = Encoding.UTF8.GetByteCount(line) + 1L;
            if (bytes > 1048576 || bytes > MaximumOutputBytes - writtenBytes)
                throw new InvalidDataException("Export byte bound reached");
            writer.WriteLine(line);
            writtenBytes += bytes;
        }
    }
}
