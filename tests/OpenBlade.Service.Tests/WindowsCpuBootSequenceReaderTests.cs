using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuBootSequenceReaderTests
{
    [Fact]
    public void ConstructionDoesNotProbeAndEveryReadAcquiresFreshValue()
    {
        _ = new WindowsCpuBootSequenceReader();
        var native = new Native(); var reader = new WindowsCpuBootSequenceReader(native);
        Assert.Empty(native.Calls);
        Assert.Equal(new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 47), reader.Read());
        native.Copy = new(true, 4, 48);
        Assert.Equal(new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 48), reader.Read());
        Assert.Equal(new[] { "architecture", "version", "copy", "architecture", "version", "copy" }, native.Calls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public void ExactUint32IncludingZeroIsAnObservation(uint value)
    {
        var native = new Native { Copy = new(true, 4, value) };
        var result = new WindowsCpuBootSequenceReader(native).Read();
        Assert.NotNull(result); Assert.True(result.IsWellFormed);
        Assert.Equal(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, result.Source);
        Assert.Equal(value, result.Sequence);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    [InlineData(true, 5)]
    [InlineData(true, -1)]
    public void FailedOrNonexactCopyNeverPublishesValueOrReusesPriorObservation(bool succeeded, int bytes)
    {
        var native = new Native(); var reader = new WindowsCpuBootSequenceReader(native);
        Assert.NotNull(reader.Read());
        native.Copy = new(succeeded, bytes < 0 ? nuint.MaxValue : (nuint)bytes, 47);
        Assert.Null(reader.Read());
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("version")]
    public void UnsupportedEnvironmentStopsBeforeMemoryRead(string rejected)
    {
        var native = new Native { Rejected = rejected };
        Assert.Null(new WindowsCpuBootSequenceReader(native).Read());
        Assert.DoesNotContain("copy", native.Calls);
        Assert.Equal(rejected == "architecture" ? 1 : 2, native.Calls.Count);
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("version")]
    [InlineData("copy")]
    public void NativeFailureAtAnyStageCannotPublishAnObservation(string stage)
    {
        var native = new Native(); var reader = new WindowsCpuBootSequenceReader(native);
        Assert.NotNull(reader.Read()); native.Calls.Clear();
        native.Callback = name => { if (name == stage) { throw new InvalidOperationException("native failure"); } };
        Assert.Null(reader.Read()); Assert.Equal(stage, native.Calls[^1]);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("architecture")]
    [InlineData("version")]
    [InlineData("copy")]
    public void CancellationStopsTheNextCallOrDiscardsTheCompletedCopy(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native { Callback = name => { if (name == stage) { cancellation.Cancel(); } } };
        if (stage == "before") { cancellation.Cancel(); }
        Assert.Null(new WindowsCpuBootSequenceReader(native).Read(cancellation.Token));
        if (stage == "before") { Assert.Empty(native.Calls); }
        else { Assert.Equal(stage, native.Calls[^1]); }
    }

    private sealed class Native : IWindowsCpuBootSequenceNative
    {
        internal readonly List<string> Calls = [];
        internal WindowsCpuBootSequenceCopy Copy = new(true, 4, 47);
        internal string? Rejected;
        internal Action<string>? Callback;
        public bool IsNativeWindowsAmd64() => Check("architecture");
        public bool MatchesReviewedWindowsVersion() => Check("version");
        public WindowsCpuBootSequenceCopy CopyFixedBootSequence() { _ = Check("copy"); return Copy; }
        private bool Check(string name) { Calls.Add(name); Callback?.Invoke(name); return name != Rejected; }
    }
}
