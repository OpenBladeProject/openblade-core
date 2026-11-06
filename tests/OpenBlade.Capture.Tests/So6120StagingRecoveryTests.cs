using System.Text.Json;

namespace OpenBlade.Capture.Tests;

public sealed class So6120StagingRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndPlanDoNotUseLivePath(bool explicitPlan)
    {
        using var output = new StringWriter();
        int result = await So6120StagingRecoveryCommand.RunAsync(explicitPlan ? ["--plan"] : [],
            new StringReader(""), output, (_, _, _, _, _) => throw new InvalidOperationException("Live path entered"));
        Assert.Equal(0, result);
        Assert.Contains("Restore only changed B8 then B9 to zero", output.ToString());
    }

    [Theory]
    [InlineData(54, 49)]
    [InlineData(0, 49)]
    [InlineData(0, 0)]
    public void OnlyChangedStagingBytesAreRestoredAndIndependentlyConfirmed(byte b8, byte b9)
    {
        var port = new Port();
        port.Mapped[0xB8] = b8;
        port.Mapped[0xB9] = b9;
        var checkpoints = new List<string?>();
        Restore(port, row =>
        {
            if (row.GetProperty("phase").GetString() == "snapshot")
            { checkpoints.Add(row.GetProperty("checkpoint").GetString()); }
        });

        byte[] expected = new[] { (Index: (byte)0xB8, Baseline: b8), (Index: (byte)0xB9, Baseline: b9) }
            .Where(item => item.Baseline != 0).Select(item => item.Index).ToArray();
        Assert.Equal(expected, port.Commands.Where(item => item.Command == 0xA1).Select(item => item.Index));
        foreach (byte index in expected)
        {
            int position = port.Commands.IndexOf((0xA1, index, 0));
            Assert.True(position >= 0);
            Assert.Equal(((byte)0xA0, index, (byte)0), port.Commands[position + 1]);
        }
        Assert.Equal(0, port.Mapped[0xB8]);
        Assert.Equal(0, port.Mapped[0xB9]);
        Assert.Equal("before", checkpoints[0]);
        Assert.Equal("final", checkpoints[^1]);
        Assert.Equal(expected.Length + 2, checkpoints.Count);
        Assert.Equal(new byte[] { 0x1A, 0x36, 0xF0, 0xB8, 0xB9, 0xB2, 0x0A, 0x06 },
            port.Commands.Take(8).Select(item => item.Index));
        Assert.Equal(8 * checkpoints.Count + 2 * expected.Length, port.Commands.Count);
    }

    [Theory]
    [InlineData(0x1A, 5)]
    [InlineData(0x36, 1)]
    [InlineData(0xF0, 0x40)]
    [InlineData(0xB8, 55)]
    [InlineData(0xB9, 50)]
    [InlineData(0x0A, 1)]
    [InlineData(0x06, 0)]
    public void UnexpectedBaselineByteStopsAtThatField(byte register, byte value)
    {
        var port = new Port();
        port.Mapped[register] = value;
        Assert.Throws<InvalidOperationException>(() => Restore(port));
        Assert.All(port.Commands, item => Assert.Equal(0xA0, item.Command));
        Assert.Equal(register, port.Commands[^1].Index);
    }

    [Fact]
    public void FailedIndependentConfirmationStopsWithoutAnyFurtherCommand()
    {
        var port = new Port { FailConfirmation = true };
        var phases = new List<string?>();
        Assert.Throws<InvalidOperationException>(() => Restore(port, row => phases.Add(row.GetProperty("phase").GetString())));
        Assert.Single(port.Commands, item => item.Command == 0xA1);
        Assert.Equal(((byte)0xA0, (byte)0xB8, (byte)0), port.Commands[^1]);
        Assert.Equal("fenced", phases[^1]);
        Assert.Equal(49, port.Mapped[0xB9]);
    }

    [Fact]
    public void UnexpectedPostWriteStateStopsBeforeTheSecondSettingWrite()
    {
        var port = new Port();
        port.AfterWrite = () => port.Mapped[0xF0] = 0x40;
        Assert.Throws<InvalidOperationException>(() => Restore(port));
        Assert.Single(port.Commands, item => item.Command == 0xA1);
        Assert.Equal(((byte)0xA0, (byte)0xF0, (byte)0), port.Commands[^1]);
        Assert.Equal(49, port.Mapped[0xB9]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedPublicationIntentOrFreshAdmissionPreventsTheWrite(bool storageFailure)
    {
        var port = new Port();
        bool publicationIntent = false;
        Assert.Throws<InvalidOperationException>(() => Restore(port, row =>
        {
            if (row.GetProperty("phase").GetString() != "publicationIntent"
                || row.GetProperty("request").GetProperty("WriteValue").ValueKind != JsonValueKind.Number)
            { return; }
            publicationIntent = true;
            if (storageFailure) { throw new IOException("Synthetic journal write failure"); }
        }, () =>
        {
            if (publicationIntent) { throw new InvalidOperationException("Synthetic admission drift"); }
        }));
        Assert.True(publicationIntent);
        Assert.Equal(8, port.Commands.Count);
        Assert.All(port.Commands, item => Assert.Equal(0xA0, item.Command));
        Assert.Equal(54, port.Mapped[0xB8]);
    }

    [Fact]
    public void CheckpointThatExpiresWhileBeingRecordedFencesBeforeA1()
    {
        var port = new Port();
        var clock = new Clock();
        bool fenced = false;
        Assert.Throws<TimeoutException>(() => Restore(port, row =>
        {
            string? phase = row.GetProperty("phase").GetString();
            if (phase == "snapshot") { clock.Ticks += TimeSpan.FromSeconds(30).Ticks; }
            if (phase == "fenced") { fenced = true; }
        }, clock: clock));
        Assert.True(fenced);
        Assert.Equal(8, port.Commands.Count);
        Assert.All(port.Commands, item => Assert.Equal(0xA0, item.Command));
    }

    [Fact]
    public void UnavailableClaimStopsBeforeTheFirstMailboxOperation()
    {
        var port = new Port();
        Assert.Throws<IOException>(() => So6120StagingRecovery.Restore(port, TimeProvider.System, () => { },
            () => throw new IOException("Already claimed"), _ => { }, CancellationToken.None));
        Assert.Equal(0, port.Operations);
    }

    [Fact]
    public void DurableClaimRemainsConsumedAndLeavesTheOriginalMarkerUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "ob-stage-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "attempt.json");
            File.WriteAllText(original, "original attempt marker");
            Guid prior = Guid.NewGuid();
            Guid run = Guid.NewGuid();
            using (So6120StagingRecovery.CreateClaim(root, prior, run, new('A', 64))) { }
            Assert.Throws<IOException>(() => So6120StagingRecovery.CreateClaim(root, prior, Guid.NewGuid(), new('B', 64)));
            Assert.Equal("original attempt marker", File.ReadAllText(original));
            using var claim = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, $"restore-staging-{prior:N}.json")));
            Assert.Equal(run, claim.RootElement.GetProperty("run").GetGuid());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Restore(Port port, Action<JsonElement>? record = null, Action? admit = null, TimeProvider? clock = null) =>
        So6120StagingRecovery.Restore(port, clock ?? TimeProvider.System, admit ?? (() => { }),
            () =>
            {
                Assert.Equal(0, port.Operations);
                port.Claimed = true;
                return new MemoryStream();
            },
            row => record?.Invoke(JsonSerializer.SerializeToElement(row)), CancellationToken.None);

    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    private sealed class Port : ISo6120MailboxBytePort
    {
        public byte[] Mapped { get; } = new byte[256];
        public List<(byte Command, byte Index, byte WriteValue)> Commands { get; } = [];
        public int Operations { get; private set; }
        public bool Claimed { get; set; }
        public bool FailConfirmation { get; init; }
        public Action? AfterWrite { get; set; }
        private readonly byte[] mailbox = new byte[256];
        private byte selected;
        private bool wrote;

        public Port()
        {
            Mapped[0x06] = 1;
            Mapped[0xB2] = 183;
            Mapped[0xB8] = 54;
            Mapped[0xB9] = 49;
        }

        public So6120PortResult SelectIndex(byte index)
        {
            Operations++;
            Assert.True(Claimed);
            selected = index;
            return Done();
        }

        public So6120PortResult ReadData()
        {
            Operations++;
            return new(true, 1, mailbox[selected], 0, So6120PortEffect.Performed);
        }

        public So6120PortResult WriteData(byte value)
        {
            Operations++;
            mailbox[selected] = value;
            if (selected != 0x93) { return Done(); }
            byte index = mailbox[0x91];
            Commands.Add((value, index, value == 0xA1 ? mailbox[0xA0] : (byte)0));
            if (value == 0xA1)
            {
                Mapped[index] = mailbox[0xA0];
                wrote = true;
                AfterWrite?.Invoke();
            }
            else if (value == 0xA0)
            {
                if (wrote && FailConfirmation) { return new(false, 0, 0, 5, So6120PortEffect.MayHavePerformed); }
                mailbox[0xA0] = Mapped[index];
            }
            else { throw new InvalidOperationException("Unexpected publication"); }
            mailbox[0x94] = 0x80;
            mailbox[0x93] = 0;
            return Done();
        }

        private static So6120PortResult Done() => new(true, 10, 0, 0, So6120PortEffect.Performed);
    }
}
