using System.Runtime.InteropServices;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuBootSequenceReaderTests
{
    [Theory]
    [InlineData(26200u)]
    [InlineData(26300u)]
    public void ExactVersionPolicyAcceptsOnlySuccessfulReviewedFiveFieldProfiles(uint build)
    {
        Assert.True(WindowsCpuBootSequenceReader.IsReviewedWindowsVersion(0, 10, 0, build, 0, 0));
    }

    [Theory]
    [InlineData(1, 10u, 0u, 26200u, 0, 0)]
    [InlineData(int.MaxValue, 10u, 0u, 26200u, 0, 0)]
    [InlineData(-1, 10u, 0u, 26200u, 0, 0)]
    [InlineData(int.MinValue, 10u, 0u, 26200u, 0, 0)]
    [InlineData(0, 9u, 0u, 26200u, 0, 0)]
    [InlineData(0, 11u, 0u, 26200u, 0, 0)]
    [InlineData(0, 10u, 1u, 26200u, 0, 0)]
    [InlineData(0, 10u, 0u, 26199u, 0, 0)]
    [InlineData(0, 10u, 0u, 26201u, 0, 0)]
    [InlineData(0, 10u, 0u, 26299u, 0, 0)]
    [InlineData(0, 10u, 0u, 26301u, 0, 0)]
    [InlineData(-1, 10u, 0u, 26300u, 0, 0)]
    [InlineData(0, 11u, 0u, 26300u, 0, 0)]
    [InlineData(0, 10u, 1u, 26300u, 0, 0)]
    [InlineData(0, 10u, 0u, 26300u, 1, 0)]
    [InlineData(0, 10u, 0u, 26300u, 0, 1)]
    [InlineData(0, 10u, 0u, 30000u, 0, 0)]
    [InlineData(0, 10u, 0u, 26200u, 1, 0)]
    [InlineData(0, 10u, 0u, 26200u, 0, 1)]
    public void ExactVersionPolicyRejectsNonzeroStatusOrUnreviewedField(int status, uint major, uint minor,
        uint build, ushort servicePackMajor, ushort servicePackMinor)
    {
        Assert.False(WindowsCpuBootSequenceReader.IsReviewedWindowsVersion(status, major, minor, build,
            servicePackMajor, servicePackMinor));
    }

    [Fact]
    public void VersionQueryUsesFullExLayoutWithNumericServicePackFields()
    {
        Assert.Equal(284, Marshal.SizeOf<WindowsCpuBootSequenceReader.OsVersionInfo>());
        Assert.Equal((nint)276, Marshal.OffsetOf<WindowsCpuBootSequenceReader.OsVersionInfo>("ServicePackMajor"));
        Assert.Equal((nint)278, Marshal.OffsetOf<WindowsCpuBootSequenceReader.OsVersionInfo>("ServicePackMinor"));
    }

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
