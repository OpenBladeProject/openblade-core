using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRazerHidSessionOpenerTests
{
    private static readonly HostIdentityReadResult ExactHost = new(
        new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "3.01"),
        null);

    private static readonly HidHandleIdentity ExactHandle = new(
        0x1532, 0x02E0, 0x0001, 0x0002, 91);

    private static readonly HidHandleIdentity[] InexactHandles =
    {
        ExactHandle with { VendorId = 0x1234 },
        ExactHandle with { ProductId = 0x029F },
        ExactHandle with { UsagePage = 0x000C },
        ExactHandle with { Usage = 0x0001 },
        ExactHandle with { FeatureReportByteLength = 90 },
    };

    public static TheoryData<int> InexactHandleIndexes => new()
    {
        0, 1, 2, 3, 4,
    };

    [Fact]
    public void Open_HostBlockStopsBeforeGuardAndNativeCalls()
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        WindowsRazerHidSessionOpener opener = new(
            new ScriptedIdentitySource(
                new HostIdentityReadResult(null, HidSessionFailureKind.IdentityBlocked),
                events),
            new ScriptedGuard(new HardwareAccessDecision([], null), events),
            native,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        AssertFailure(result, HidSessionFailureKind.IdentityBlocked);
        Assert.Equal(["identity"], events);
        Assert.Equal(0, native.EnumerateCalls);
        Assert.Empty(native.OpenRequests);
    }

    [Theory]
    [InlineData(false, HidSessionFailureKind.ConflictBlocked)]
    [InlineData(true, HidSessionFailureKind.ConflictDetectionFailed)]
    public void Open_GuardBlockStopsBeforeEnumeration(
        bool detectionFailed,
        HidSessionFailureKind expectedFailure)
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        HardwareAccessDecision decision = detectionFailed
            ? new([], "scripted failure")
            : new(["Razer Synapse Service"], null);
        WindowsRazerHidSessionOpener opener = new(
            new ScriptedIdentitySource(ExactHost, events),
            new ScriptedGuard(decision, events),
            native,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        AssertFailure(result, expectedFailure);
        Assert.Equal(["identity", "guard"], events);
        Assert.Equal(0, native.EnumerateCalls);
        Assert.Empty(native.OpenRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_AdvisorySkipsControllerPresenceButKeepsExactAdmission(
        bool detectionFailed)
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        native.AddCandidate("exact", ExactHandle);
        HardwareAccessDecision decision = detectionFailed
            ? new([], "scripted failure")
            : new(["Razer Synapse Service"], null);
        WindowsRazerHidSessionOpener opener = new(
            new ScriptedIdentitySource(ExactHost, events),
            new ScriptedGuard(decision, events),
            native,
            ControllerExclusivityPolicy.Advisory);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        Assert.DoesNotContain("guard", events);
        Assert.Equal(1, native.BindCalls);
        await result.Value.DisposeAsync();
    }

    [Fact]
    public void Open_AdvisoryStillRejectsWrongPidAndAmbiguousExactHandles()
    {
        ScriptedNative wrongPid = new([]);
        wrongPid.AddCandidate("wrong", ExactHandle with { ProductId = 0x029F });
        AssertFailure(
            CreateOpener(wrongPid, policy: ControllerExclusivityPolicy.Advisory).Open(),
            HidSessionFailureKind.IdentityBlocked);

        ScriptedNative ambiguous = new([]);
        ambiguous.AddCandidate("first", ExactHandle);
        ambiguous.AddCandidate("second", ExactHandle);
        AssertFailure(
            CreateOpener(ambiguous, policy: ControllerExclusivityPolicy.Advisory).Open(),
            HidSessionFailureKind.IdentityBlocked);
    }

    [Fact]
    public async Task Open_UsesExactParametersAndBindsTheSoleExactSameHandleAfterAllReads()
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        OpenedCandidateHandle rejected = native.AddCandidate("rejected", ExactHandle with { Usage = 1 });
        OpenedCandidateHandle selected = native.AddCandidate("selected", ExactHandle);
        WindowsRazerHidSessionOpener opener = CreateOpener(native, events);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        Assert.All(native.OpenRequests, request => Assert.Equal(
            new HidOpenParameters(
                0xC0000000u,
                0x00000003u,
                3u,
                0x40000000u,
                false),
            request.Parameters));
        Assert.Equal(
            ["identity", "guard", "enumerate", "open:rejected", "read:rejected", "open:selected", "read:selected", "bind:selected"],
            events);
        Assert.True(rejected.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["rejected"]);
        Assert.False(selected.FileHandle.IsClosed);
        Assert.Equal(0, native.DisposeCounts["selected"]);
        Assert.Same(selected, native.BoundCandidate);

        await result.Value.DisposeAsync();
        Assert.True(selected.FileHandle.IsClosed);
    }

    [Theory]
    [MemberData(nameof(InexactHandleIndexes))]
    public void Open_RejectsEveryInexactIdentityReadFromTheSameCandidateHandle(
        int identityIndex)
    {
        ScriptedNative native = new([]);
        OpenedCandidateHandle candidate = native.AddCandidate(
            "candidate",
            InexactHandles[identityIndex]);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        AssertFailure(result, HidSessionFailureKind.IdentityBlocked);
        Assert.True(candidate.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["candidate"]);
        Assert.Equal([candidate], native.IdentityHandles);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Open_ZeroCandidatesIsIdentityBlocked()
    {
        HidSessionResult<OpenedHidHandle> result = CreateOpener(new ScriptedNative([])).Open();

        AssertFailure(result, HidSessionFailureKind.IdentityBlocked);
    }

    [Fact]
    public void Open_MultipleExactCandidatesDisposesAllWithoutBinding()
    {
        ScriptedNative native = new([]);
        OpenedCandidateHandle first = native.AddCandidate("first", ExactHandle);
        OpenedCandidateHandle second = native.AddCandidate("second", ExactHandle);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        AssertFailure(result, HidSessionFailureKind.IdentityBlocked);
        Assert.True(first.FileHandle.IsClosed);
        Assert.True(second.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["first"]);
        Assert.Equal(1, native.DisposeCounts["second"]);
        Assert.Equal(0, native.BindCalls);
    }

    [Theory]
    [InlineData(NativeFailureStage.Enumerate)]
    [InlineData(NativeFailureStage.Open)]
    [InlineData(NativeFailureStage.ReadIdentity)]
    [InlineData(NativeFailureStage.Bind)]
    public void Open_NativeFailuresAreContainedAndDisposeOwnedCandidates(NativeFailureStage stage)
    {
        ScriptedNative native = new([]) { FailureStage = stage };
        OpenedCandidateHandle candidate = native.AddCandidate("candidate", ExactHandle);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        AssertFailure(result, HidSessionFailureKind.NativeIoFailure);
        if (stage is NativeFailureStage.ReadIdentity or NativeFailureStage.Bind)
        {
            Assert.True(candidate.FileHandle.IsClosed);
            Assert.Equal(1, native.DisposeCounts["candidate"]);
        }
        else
        {
            candidate.Dispose();
        }
    }

    [Fact]
    public void Open_IdentityFailureAfterExactMatchDisposesEveryOwnedCandidateExactlyOnce()
    {
        ScriptedNative native = new([]) { ReadFailurePath = "failing" };
        OpenedCandidateHandle retained = native.AddCandidate("retained", ExactHandle);
        OpenedCandidateHandle failing = native.AddCandidate("failing", ExactHandle);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        AssertFailure(result, HidSessionFailureKind.NativeIoFailure);
        Assert.True(retained.FileHandle.IsClosed);
        Assert.True(failing.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["retained"]);
        Assert.Equal(1, native.DisposeCounts["failing"]);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public async Task OpenedHidHandle_DisposesBoundHandleBeforeFileHandleAndIsIdempotent()
    {
        SafeFileHandle fileHandle = CreateBindableTestHandle();
        ThreadPoolBoundHandle boundHandle = ThreadPoolBoundHandle.BindHandle(fileHandle);
        List<string> disposal = [];
        OpenedHidHandle opened = new(
            fileHandle,
            boundHandle,
            () =>
            {
                Assert.False(fileHandle.IsClosed);
                disposal.Add("bound");
                boundHandle.Dispose();
            },
            () =>
            {
                disposal.Add("file");
                fileHandle.Dispose();
            });

        await opened.DisposeAsync();
        await opened.DisposeAsync();

        Assert.Equal(["bound", "file"], disposal);
        Assert.True(fileHandle.IsClosed);
    }

    [Fact]
    public async Task Open_AccessDeniedRetriesMetadataOnlyAndBindsThatSameExactHandle()
    {
        ScriptedNative native = new([]) { AccessDeniedPath = "candidate" };
        OpenedCandidateHandle candidate = native.AddCandidate("candidate", ExactHandle);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        Assert.Equal([0xC0000000u, 0u], native.OpenRequests.Select(request => request.Parameters.DesiredAccess));
        Assert.All(native.OpenRequests, request =>
        {
            Assert.Equal(3u, request.Parameters.ShareMode);
            Assert.Equal(3u, request.Parameters.CreationDisposition);
            Assert.Equal(0x40000000u, request.Parameters.FlagsAndAttributes);
            Assert.False(request.Parameters.Inheritable);
        });
        Assert.Equal([candidate], native.IdentityHandles);
        Assert.Same(candidate, native.BoundCandidate);
        Assert.Equal(1, native.BindCalls);

        await result.Value.DisposeAsync();
    }

    private static WindowsRazerHidSessionOpener CreateOpener(
        ScriptedNative native,
        List<string>? events = null,
        ControllerExclusivityPolicy policy = ControllerExclusivityPolicy.Strict) =>
        new(
            new ScriptedIdentitySource(ExactHost, events),
            new ScriptedGuard(new HardwareAccessDecision([], null), events),
            native,
            policy);

    private static void AssertFailure(
        HidSessionResult<OpenedHidHandle> result,
        HidSessionFailureKind expected)
    {
        Assert.Null(result.Value);
        Assert.Equal(expected, result.Failure);
    }

    private static SafeFileHandle CreateBindableTestHandle()
    {
        string path = Path.GetTempFileName();
        SafeFileHandle handle = CreateFile(
            path,
            0xC0000000u,
            0x00000007u,
            IntPtr.Zero,
            3u,
            0x44000000u,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException(
                $"Could not create bindable test handle: {Marshal.GetLastWin32Error()}.");
        }

        return handle;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    public enum NativeFailureStage
    {
        Enumerate,
        Open,
        ReadIdentity,
        Bind,
    }

    private sealed class ScriptedIdentitySource(
        HostIdentityReadResult result,
        List<string>? events = null) : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity()
        {
            events?.Add("identity");
            return result;
        }
    }

    private sealed class ScriptedGuard(
        HardwareAccessDecision decision,
        List<string>? events = null) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check()
        {
            events?.Add("guard");
            return decision;
        }
    }

    private sealed class ScriptedNative(List<string> events) : IHidCandidateNative
    {
        private readonly Dictionary<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)>
            candidates = [];

        public NativeFailureStage? FailureStage { get; set; }

        public string? ReadFailurePath { get; set; }

        public string? AccessDeniedPath { get; set; }

        public int EnumerateCalls { get; private set; }

        public int BindCalls { get; private set; }

        public OpenedCandidateHandle? BoundCandidate { get; private set; }

        public List<(string Path, HidOpenParameters Parameters)> OpenRequests { get; } = [];

        public List<OpenedCandidateHandle> IdentityHandles { get; } = [];

        public Dictionary<string, int> DisposeCounts { get; } = [];

        public OpenedCandidateHandle AddCandidate(string path, HidHandleIdentity identity)
        {
            SafeFileHandle fileHandle = CreateBindableTestHandle();
            DisposeCounts.Add(path, 0);
            OpenedCandidateHandle handle = new(
                fileHandle,
                () =>
                {
                    DisposeCounts[path]++;
                    fileHandle.Dispose();
                });
            candidates.Add(path, (handle, identity));
            return handle;
        }

        public IReadOnlyList<string> EnumerateDevicePaths()
        {
            EnumerateCalls++;
            events.Add("enumerate");
            ThrowAt(NativeFailureStage.Enumerate);
            return candidates.Keys.ToArray();
        }

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            OpenRequests.Add((path, parameters));
            events.Add($"open:{path}");
            ThrowAt(NativeFailureStage.Open);
            if (path == AccessDeniedPath && parameters.DesiredAccess != 0)
            {
                return new(null, 5);
            }

            return new(candidates[path].Handle, null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            IdentityHandles.Add(handle);
            string path = candidates.Single(pair => ReferenceEquals(pair.Value.Handle, handle)).Key;
            events.Add($"read:{path}");
            ThrowAt(NativeFailureStage.ReadIdentity);
            if (ReadFailurePath == path)
            {
                throw new InvalidOperationException("scripted path-specific identity failure");
            }

            return candidates[path].Identity;
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            BindCalls++;
            string path = candidates.Single(pair => ReferenceEquals(pair.Value.Handle, handle)).Key;
            events.Add($"bind:{path}");
            ThrowAt(NativeFailureStage.Bind);
            BoundCandidate = handle;
            SafeFileHandle selected = handle.TransferOwnership();
            ThreadPoolBoundHandle bound = ThreadPoolBoundHandle.BindHandle(selected);
            return new OpenedHidHandle(selected, bound);
        }

        private void ThrowAt(NativeFailureStage stage)
        {
            if (FailureStage == stage)
            {
                throw new InvalidOperationException("scripted native failure");
            }
        }
    }
}
