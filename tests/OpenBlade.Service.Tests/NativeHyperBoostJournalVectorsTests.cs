using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// Native owned HyperBoost and the native convergence loop read managed's
// HyperBoost ownership journal read-only and write only while managed would
// call it safe (IsSafeMaintainHyperBoostJournalBaseline). These rows pin what
// managed's own store reads from journals it writes and from documents that
// differ from them in member case, duplicates, member types and the update
// time; the Rust tests replay each row through the native reader.
public sealed class NativeHyperBoostJournalVectorsTests
{
    private const string Vectors = "hyperboost-journal-vectors.json";
    private const string Regenerate = "OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_JOURNAL_VECTORS";
    private const string Comment =
        "Recorded by NativeHyperBoostJournalVectorsTests; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_JOURNAL_VECTORS set to this directory. Each row is a "
        + "HyperBoost ownership journal document and whether managed HyperBoostOwnershipJournalStore.LoadAsync "
        + "reads it as a valid Relinquished journal with a hash (IsSafeMaintainHyperBoostJournalBaseline). "
        + "Rows named written/* are the store's own WriteAsync output; the others edit the written/balanced "
        + "document's text.";

    private static readonly DateTimeOffset UpdatedAt =
        new DateTimeOffset(2026, 11, 24, 8, 30, 15, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public async Task NativeHyperBoostJournalVectors_MatchManagedStore()
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string directory = Path.Combine(Path.GetTempPath(), "openblade-hyperboost-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rows = new JsonArray();
            var written = new List<(string Name, string Document)>();
            foreach ((string name, HyperBoostOwnershipJournal journal) in WrittenJournals())
            {
                string document = await WriteAsync(directory, journal);
                written.Add((name, document));
                rows.Add(await RowAsync(directory, "written/" + name, document));
            }

            string balanced = written[0].Document;
            foreach ((string name, string document) in Edits(balanced))
            {
                rows.Add(await RowAsync(directory, name, document));
            }

            string path = string.IsNullOrEmpty(output)
                ? Path.Combine(AppContext.BaseDirectory, Vectors)
                : Path.Combine(output, Vectors);
            if (!string.IsNullOrEmpty(output))
            {
                var text = new StringBuilder("{\n\"comment\":")
                    .Append(JsonValue.Create(Comment).ToJsonString())
                    .Append(",\n\"rows\":[\n")
                    .AppendJoin(",\n", rows.Select(row => row!.ToJsonString()))
                    .Append("\n]\n}\n");
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            JsonObject recorded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(["comment", "rows"], recorded.Select(member => member.Key).ToArray());
            Assert.Equal(Comment, recorded["comment"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(recorded["rows"], rows), rows.ToJsonString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<(string Name, HyperBoostOwnershipJournal Journal)> WrittenJournals()
    {
        HyperBoostOwnershipJournal Relinquished(
            PerformancePreset baseMode,
            HyperBoostRestoreIntent intent,
            PowerSource source = PowerSource.PluggedIn) => new(
                HyperBoostOwnershipJournalState.Relinquished,
                baseMode,
                RequiresCoolingPad: false,
                UpdatedAt)
            {
                RestoreIntent = intent,
                RestorePowerSource = source,
                TransitionId = Guid.Parse("0b4f3e2a-5c6d-4e7f-8a9b-0c1d2e3f4a5b"),
            };

        yield return ("balanced", Relinquished(PerformancePreset.Balanced, HyperBoostRestoreIntent.BalancedAutomatic));
        yield return ("custom", Relinquished(
            PerformancePreset.Custom,
            new HyperBoostRestoreIntent(
                PerformancePreset.Custom,
                new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Medium),
                GpuClockTuning: null,
                FanControlSettings.FromLegacy(FanMode.Automatic))));
        yield return ("gpu-fixed-usbc", Relinquished(
            PerformancePreset.Performance,
            new HyperBoostRestoreIntent(
                PerformancePreset.Performance,
                CustomPerformance: null,
                new GpuClockTuningSettings(true, new GpuClockOffsetSettings(100, 200)),
                new FanControlSettings(FanMode.Fixed, 4200, ManualFanCurve.Default)),
            PowerSource.UsbC));
        yield return ("turbo-battery", Relinquished(
            PerformancePreset.Turbo,
            new HyperBoostRestoreIntent(
                PerformancePreset.BatterySaver,
                CustomPerformance: null,
                GpuClockTuning: null,
                FanControlSettings.FromLegacy(FanMode.Automatic)),
            PowerSource.Battery));
    }

    // Each edit changes the written Balanced document's text in one place.
    private static IEnumerable<(string Name, string Document)> Edits(string balanced)
    {
        string Replace(string from, string to)
        {
            Assert.Contains(from, balanced, StringComparison.Ordinal);
            return balanced.Replace(from, to, StringComparison.Ordinal);
        }

        yield return ("duplicate/case-variant-later-pending", Replace("\"state\": 3,", "\"state\": 3,\n  \"State\": 1,"));
        yield return ("duplicate/case-variant-later-relinquished", Replace("\"state\": 3,", "\"State\": 1,\n  \"state\": 3,"));
        yield return ("duplicate/exact", Replace("\"state\": 3,", "\"state\": 1,\n  \"state\": 3,"));
        yield return ("duplicate/nested-case-variant", Replace("\"mode\": 0,", "\"mode\": 0,\n    \"Mode\": 1,"));
        yield return ("case/pascal-member", Replace("\"state\": 3,", "\"State\": 3,"));
        yield return ("unknown/member", Replace("\"state\": 3,", "\"state\": 3,\n  \"extra\": 1,"));
        yield return ("unknown/nested-member", Replace("\"mode\": 0,", "\"mode\": 0,\n    \"extra\": 1,"));
        yield return ("type/state-string", Replace("\"state\": 3,", "\"state\": \"3\","));
        yield return ("type/state-name", Replace("\"state\": 3,", "\"state\": \"Relinquished\","));
        yield return ("type/schema-string", Replace("\"schemaVersion\": 8,", "\"schemaVersion\": \"8\","));
        yield return ("type/state-fraction", Replace("\"state\": 3,", "\"state\": 3.0,"));
        yield return ("type/pad-string", Replace("\"requiresCoolingPad\": false,", "\"requiresCoolingPad\": \"false\","));
        yield return ("type/current-destination-null", Replace("\"restoreUsesCurrentPowerDestination\": false", "\"restoreUsesCurrentPowerDestination\": null"));
        yield return ("type/transition-id-number", Replace("\"transitionId\": \"0b4f3e2a-5c6d-4e7f-8a9b-0c1d2e3f4a5b\",", "\"transitionId\": 7,"));
        yield return ("type/transition-id-braced", Replace("\"transitionId\": \"0b4f3e2a-5c6d-4e7f-8a9b-0c1d2e3f4a5b\",", "\"transitionId\": \"{0b4f3e2a-5c6d-4e7f-8a9b-0c1d2e3f4a5b}\","));
        yield return ("type/fan-control-mode-only", ReplaceFanControl(balanced, "{\"mode\": 0}"));
        yield return ("type/nested-number-string", Replace("\"fixedSpeedRpm\": 3700,", "\"fixedSpeedRpm\": \"3700\","));
        yield return ("type/nested-enum-string", Replace("\"mode\": 0,", "\"mode\": \"0\","));
        yield return ("type/updated-number", Replace("\"updatedAtUtc\": \"2026-11-24T08:30:15.1234567+00:00\",", "\"updatedAtUtc\": 5,"));
        yield return ("date/zulu", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24T08:30:15Z"));
        yield return ("date/offset", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24T00:30:15-08:00"));
        yield return ("date/date-only", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24"));
        yield return ("date/no-offset", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24T08:30:15"));
        yield return ("date/lower-case", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24t08:30:15z"));
        yield return ("date/invalid-day", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-02-30T08:30:15+00:00"));
        yield return ("date/leap-day", Replace("2026-11-24T08:30:15.1234567+00:00", "2028-02-29T08:30:15+00:00"));
        yield return ("date/default", Replace("2026-11-24T08:30:15.1234567+00:00", "0001-01-01T00:00:00+00:00"));
        yield return ("date/default-offset", Replace("2026-11-24T08:30:15.1234567+00:00", "0001-01-01T05:00:00+05:00"));
        yield return ("date/before-minimum", Replace("2026-11-24T08:30:15.1234567+00:00", "0001-01-01T00:00:00+01:00"));
        yield return ("date/after-maximum", Replace("2026-11-24T08:30:15.1234567+00:00", "9999-12-31T23:59:59-01:00"));
        yield return ("date/eight-fraction-digits", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24T08:30:15.12345678+00:00"));
        yield return ("date/offset-beyond-fourteen", Replace("2026-11-24T08:30:15.1234567+00:00", "2026-11-24T08:30:15+15:00"));
    }

    private static string ReplaceFanControl(string document, string replacement)
    {
        JsonObject node = JsonNode.Parse(document)!.AsObject();
        node["restoreFanControl"] = JsonNode.Parse(replacement);
        return node.ToJsonString();
    }

    private static async Task<string> WriteAsync(string directory, HyperBoostOwnershipJournal journal)
    {
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        var store = new HyperBoostOwnershipJournalStore(path);
        await store.WriteAsync(journal, CancellationToken.None);
        return Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
    }

    private static async Task<JsonObject> RowAsync(string directory, string name, string document)
    {
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(document));
        HyperBoostOwnershipJournalLoadResult load =
            await new HyperBoostOwnershipJournalStore(path).LoadAsync(CancellationToken.None);
        bool safe = !load.Invalid
            && load.Journal is { State: HyperBoostOwnershipJournalState.Relinquished }
            && !string.IsNullOrWhiteSpace(load.Sha256);
        return new JsonObject
        {
            ["name"] = name,
            ["document"] = document,
            ["managedSafe"] = safe,
        };
    }
}
