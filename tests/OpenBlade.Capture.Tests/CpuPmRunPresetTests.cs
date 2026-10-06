using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmRunPresetTests
{
    private static readonly Guid Run = new("11111111-1111-1111-1111-111111111111");
    private static readonly string Source = new('a', 40), Manifest = new('A', 64);
    private static string Directory => Path.Combine(CpuPmSupervisorBootstrap.FixedRoot, Run.ToString("N"));
    private static string PresetPath => Path.Combine(Directory, "reviewed-preset.json");
    private static string FlatPath => Path.Combine(CpuPmSupervisorBootstrap.FixedRoot, "reviewed-preset.json");
    private static byte[] Preset(string? source = null) => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, source = source ?? Source, manifest = Manifest });

    [Fact]
    public void LoadsHeldRunPresetIndependentlyOfUnrelatedFlatPreset()
    {
        using var files = new Files(); files.Data[PresetPath] = Preset(); files.Data[FlatPath] = Preset(new('b', 40));
        var loaded = CpuPmSupervisorBootstrap.LoadRunPreset(files, Run, CancellationToken.None);
        Assert.Equal(new CpuPmReviewedLaunchBootstrap(Source, Manifest), loaded.Preset);
        Assert.Same(files.LastPin, loaded.Pin); Assert.Equal(Hash(Preset()), loaded.Pin.Sha256);
        Assert.Equal(new[] { PresetPath }, files.FileRequests);
        var parents = new Stack<string>();
        for (string? path = Path.GetDirectoryName(CpuPmSupervisorBootstrap.FixedRoot); path is not null; path = Path.GetDirectoryName(path)) { parents.Push(path); }
        var expected = parents.Select(path => (path, false, false)).ToList();
        expected.Add((CpuPmSupervisorBootstrap.FixedRoot, true, true)); expected.Add((Directory, true, false));
        Assert.Equal(expected, files.Directories); Assert.Equal(3, files.Assertions);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("flat")]
    [InlineData("sibling")]
    public void MissingRunPresetNeverFallsBack(string kind)
    {
        using var files = new Files();
        if (kind == "flat") { files.Data[FlatPath] = Preset(); }
        if (kind == "sibling") { files.Data[Path.Combine(CpuPmSupervisorBootstrap.FixedRoot, new Guid("22222222-2222-2222-2222-222222222222").ToString("N"), "reviewed-preset.json")] = Preset(); }
        Assert.Throws<IOException>(() => CpuPmSupervisorBootstrap.LoadRunPreset(files, Run, CancellationToken.None));
        Assert.Equal(new[] { PresetPath }, files.FileRequests);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("manifest")]
    [InlineData("empty")]
    [InlineData("oversize")]
    public void MalformedRunPresetBlocksEvenWhenFlatPresetIsValid(string kind)
    {
        string text = Encoding.UTF8.GetString(Preset());
        text = kind switch
        {
            "duplicate" => text.Insert(1, "\"version\":1,"),
            "unknown" => text.Insert(1, "\"approved\":true,"),
            "missing" => text.Replace("\"version\":1,", "", StringComparison.Ordinal),
            "version" => text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            "source" => text.Replace(Source, new string('A', 40), StringComparison.Ordinal),
            "manifest" => text.Replace(Manifest, new string('a', 64), StringComparison.Ordinal),
            "empty" => "",
            _ => new(' ', CpuPmLaunchSchema.RecordLimit + 1)
        };
        using var files = new Files(); files.Data[FlatPath] = Preset(); files.Data[PresetPath] = Encoding.UTF8.GetBytes(text);
        Assert.ThrowsAny<Exception>(() => CpuPmSupervisorBootstrap.LoadRunPreset(files, Run, CancellationToken.None));
        Assert.Equal(new[] { PresetPath }, files.FileRequests);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("bytes")]
    [InlineData("length")]
    [InlineData("read")]
    public void HeldPresetMismatchOrReadFailureBlocksBeforeReturningTrust(string kind)
    {
        using var files = new Files { PinFault = kind }; files.Data[PresetPath] = Preset(); files.Data[FlatPath] = Preset();
        Assert.Throws<IOException>(() => CpuPmSupervisorBootstrap.LoadRunPreset(files, Run, CancellationToken.None));
        Assert.Equal(new[] { PresetPath }, files.FileRequests); Assert.Equal(1, files.Assertions);
    }

    [Theory]
    [InlineData("ancestor")]
    [InlineData("root")]
    [InlineData("run")]
    [InlineData("file")]
    [InlineData("held-before-read")]
    [InlineData("held-after-read")]
    [InlineData("held-after-parse")]
    public void PinFailureBlocksLoad(string stage)
    {
        using var files = new Files { FailStage = stage }; files.Data[PresetPath] = Preset(); files.Data[FlatPath] = Preset();
        Assert.Throws<IOException>(() => CpuPmSupervisorBootstrap.LoadRunPreset(files, Run, CancellationToken.None));
        Assert.DoesNotContain(FlatPath, files.FileRequests);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("root")]
    [InlineData("run")]
    [InlineData("file")]
    [InlineData("read")]
    [InlineData("held-after-parse")]
    public void CancellationNeverReturnsPreset(string stage)
    {
        using var cancel = new CancellationTokenSource(); using var files = new Files(); files.Data[PresetPath] = Preset();
        files.OnStage = reached => { if (stage == reached) { cancel.Cancel(); } };
        if (stage == "before") { cancel.Cancel(); }
        Assert.Throws<OperationCanceledException>(() => CpuPmSupervisorBootstrap.LoadRunPreset(files, Run, cancel.Token));
        Assert.DoesNotContain(FlatPath, files.FileRequests);
        if (stage == "before") { Assert.Empty(files.Directories); Assert.Empty(files.FileRequests); }
    }

    [Fact]
    public void EmptyRunCannotLocateOrApproveAnyPreset()
    {
        using var files = new Files(); files.Data[FlatPath] = Preset();
        Assert.Throws<IOException>(() => CpuPmSupervisorBootstrap.LoadRunPreset(files, Guid.Empty, CancellationToken.None));
        Assert.Empty(files.Directories); Assert.Empty(files.FileRequests);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class Files : ICpuPmLaunchFiles
    {
        internal readonly Dictionary<string, byte[]> Data = new(StringComparer.Ordinal);
        internal readonly List<(string Path, bool Private, bool Protected)> Directories = [];
        internal readonly List<string> FileRequests = [];
        internal string? FailStage, PinFault; internal Action<string>? OnStage;
        internal ICpuPmLaunchFilePin? LastPin; internal int Assertions;
        private void Stage(string stage) { OnStage?.Invoke(stage); if (stage == FailStage) { throw new IOException(); } }
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl)
        {
            Directories.Add((path, privateAcl, protectedAcl));
            Stage(path == CpuPmSupervisorBootstrap.FixedRoot ? "root" : path == Directory ? "run" : "ancestor");
        }
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl)
        {
            FileRequests.Add(path); Stage("file"); Assert.True(privateAcl); Assert.Equal(CpuPmLaunchSchema.RecordLimit, maximumBytes);
            if (!Data.TryGetValue(path, out byte[]? bytes) || bytes.Length > maximumBytes) { throw new IOException(); }
            return LastPin = new Pin((byte[])bytes.Clone(), PinFault, () => Stage("read"));
        }
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path) => throw new InvalidOperationException("Preset loading must not enumerate or discover fallback files.");
        public void AssertAllPins() => Stage(++Assertions == 1 ? "held-before-read" : Assertions == 2 ? "held-after-read" : "held-after-parse");
        public void Dispose() { }
        private sealed class Pin(byte[] bytes, string? fault, Action onRead) : ICpuPmLaunchFilePin
        {
            public long Length => bytes.Length + (fault == "length" ? 1 : 0);
            public string Sha256 => fault == "hash" ? new('F', 64) : Hash(bytes);
            public byte[] ReadBounded(int maximumBytes)
            {
                onRead(); Assert.Equal(CpuPmLaunchSchema.RecordLimit, maximumBytes);
                if (fault == "read" || bytes.Length > maximumBytes) { throw new IOException(); }
                return fault == "bytes" ? Preset(new('b', 40)) : (byte[])bytes.Clone();
            }
        }
    }
}
