using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalLightingBaselineJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBladeLightingJournalTests", Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(directory, "RZ09-0529-1532-02C7.json");

    [Fact]
    public void DiscoveryDoesNotCreateDirectoryOrFiles()
    {
        var journal = Create();
        Assert.Equal(new(null, false), journal.Load());
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void PendingBaselineSurvivesRestartWithoutIntentOrReplay()
    {
        var baseline = Baseline();
        Create().SavePending(baseline);
        var restarted = Create();
        Assert.Equal(new(baseline, false), restarted.Load());
        Assert.Single(Directory.GetFiles(directory));
        Assert.DoesNotContain("intent", File.ReadAllText(JournalPath), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => restarted.SavePending(baseline));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(254)]
    [InlineData(255)]
    public void OffRetainsExactRawBrightnessEvenWhenPercentIsZero(byte raw)
    {
        var baseline = new ExperimentalLightingBaseline(LightingSettingsFactory.Off(), raw);
        Create().SavePending(baseline);
        Assert.Equal(baseline, Create().Load().Baseline);
    }

    [Fact]
    public void CompleteBuiltInParametersRoundTrip()
    {
        LightingSettings[] settings =
        [
            LightingSettingsFactory.Static(new(1, 2, 3), 50),
            new(LightingEffect.Breathing, new(1, 2, 3), 50)
            {
                Parameters = new BreathingLightingParameters(BreathingColorMode.Dual, new(1, 2, 3), new(4, 5, 6)),
            },
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50),
        ];
        foreach (LightingSettings setting in settings)
        {
            var journal = Create();
            var baseline = new ExperimentalLightingBaseline(setting, 128);
            journal.SavePending(baseline);
            Assert.Equal(baseline, Create().Load().Baseline);
            journal.ClearAfterVerifiedRestoration();
        }
    }

    [Fact]
    public void OriginalBaselineCannotBeReplaced()
    {
        var journal = Create();
        journal.SavePending(Baseline());
        Assert.Throws<InvalidDataException>(() => journal.SavePending(Baseline() with { RawBrightness = 127 }));
        Assert.Equal(Baseline(), Create().Load().Baseline);
    }

    [Fact]
    public void RepeatedSaveDoesNotRewritePendingRecord()
    {
        var journal = Create();
        journal.SavePending(Baseline());
        byte[] before = File.ReadAllBytes(JournalPath);
        journal.SavePending(Baseline());
        Assert.Equal(before, File.ReadAllBytes(JournalPath));
    }

    [Fact]
    public void VerifiedClearPublishesDurableTombstoneAndAllowsNewBaseline()
    {
        var journal = Create();
        journal.SavePending(Baseline());
        journal.ClearAfterVerifiedRestoration();
        Assert.True(File.Exists(JournalPath));
        Assert.Equal(new(null, false), Create().Load());
        var replacement = new ExperimentalLightingBaseline(LightingSettingsFactory.Off(), 17);
        Create().SavePending(replacement);
        Assert.Equal(replacement, Create().Load().Baseline);
    }

    [Fact]
    public void MissingPendingRecordFailsClosedAndRetainsOriginalInMemory()
    {
        var journal = Create();
        journal.SavePending(Baseline());
        File.Delete(JournalPath);
        Assert.Equal(new(Baseline(), true), journal.Load());
        Assert.Throws<InvalidDataException>(() => journal.SavePending(Baseline()));
        Assert.Throws<InvalidDataException>(journal.ClearAfterVerifiedRestoration);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("modelNumber", "\"RZ09-0530\"")]
    [InlineData("vendorId", "1")]
    [InlineData("productId", "709")]
    [InlineData("baseline", "null")]
    [InlineData("pending", "false")]
    public void WrongIdentitySchemaOrIncompletePendingIsInvalid(string member, string value)
    {
        Create().SavePending(Baseline());
        JsonNode document = JsonNode.Parse(File.ReadAllText(JournalPath))!;
        document[member] = JsonNode.Parse(value);
        File.WriteAllText(JournalPath, document.ToJsonString());
        var journal = Create();
        Assert.True(journal.Load().Invalid);
        Assert.Throws<InvalidDataException>(() => journal.SavePending(Baseline()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1}")]
    public void CorruptOrDuplicateRecordsAreNeverTreatedAsMissing(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(JournalPath, json);
        var journal = Create();
        Assert.True(journal.Load().Invalid);
        Assert.Throws<InvalidDataException>(journal.ClearAfterVerifiedRestoration);
        Assert.Equal(json, File.ReadAllText(JournalPath));
    }

    [Fact]
    public void UnknownHostEffectAndIncompleteBuiltInBaselinesAreRejectedWithoutFiles()
    {
        var journal = Create();
        LightingSettings[] invalid =
        [
            LightingSettingsFactory.SpectrumCycling(50),
            new(LightingEffect.Wheel, default, 50) { Parameters = new WheelLightingParameters(LightingRotationDirection.Clockwise) },
            new(LightingEffect.Static, new(1, 2, 3), 50),
            LightingSettingsFactory.Static(new(1, 2, 3), 49),
        ];
        foreach (LightingSettings setting in invalid)
        {
            Assert.Throws<InvalidDataException>(() => journal.SavePending(new(setting, 128)));
        }
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void StorageFailurePreventsBaselineAdmission()
    {
        var journal = new ExperimentalLightingBaselineJournal(directory, "RZ09-0529", 0x02C7,
            _ => throw new UnauthorizedAccessException());
        Assert.True(journal.Load().Invalid);
        Assert.Throws<InvalidDataException>(() => journal.SavePending(Baseline()));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void PublicationFailureDoesNotAdmitAnInMemoryBaseline()
    {
        var journal = Create();
        journal.Load();
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        using (File.Create(directory)) { }
        try
        {
            // A file occupying the storage-directory path prevents durable publication.
            Assert.ThrowsAny<IOException>(() => journal.SavePending(Baseline()));
            Assert.True(journal.Load().Invalid);
            Assert.Null(journal.Load().Baseline);
        }
        finally { File.Delete(directory); }
    }

    [Fact]
    public void OversizedRecordFailsClosed()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(JournalPath, new string(' ', 16385));
        Assert.True(Create().Load().Invalid);
    }

    [Fact]
    public void ProductionStorageRejectsUnprotectedDirectoryWithoutRepair()
    {
        Directory.CreateDirectory(directory);
        var journal = new ExperimentalLightingBaselineJournal(directory, "RZ09-0529", 0x02C7);
        Assert.True(journal.Load().Invalid);
        Assert.Throws<InvalidDataException>(() => journal.SavePending(Baseline()));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void StaleSnapshotCannotOverwriteAnotherJournalWriter()
    {
        var first = Create();
        var second = Create();
        first.Load();
        second.Load();
        first.SavePending(Baseline());
        Assert.Throws<InvalidDataException>(() => second.SavePending(new(LightingSettingsFactory.Off(), 0)));
        Assert.Throws<InvalidDataException>(second.ClearAfterVerifiedRestoration);
        Assert.Equal(Baseline(), Create().Load().Baseline);
    }

    private ExperimentalLightingBaselineJournal Create() => new(directory, "RZ09-0529", 0x02C7, _ => { });

    private static ExperimentalLightingBaseline Baseline() =>
        new(LightingSettingsFactory.Static(new(1, 2, 3), 50), 128);

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
