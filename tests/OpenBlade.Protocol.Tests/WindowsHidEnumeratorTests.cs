using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsHidEnumeratorTests
{
    [Fact]
    public void FindTargetInterfaces_UsesInjectedDiscoverySeamAndReturnsDiagnosticMetadataOnly()
    {
        DiagnosticNative native = new();
        OpenedCandidateHandle exact = native.Add(
            "exact",
            new HidHandleIdentity(0x1532, 0x02E0, 1, 2, 91));
        OpenedCandidateHandle rejected = native.Add(
            "rejected",
            new HidHandleIdentity(0x1532, 0x02E0, 1, 1, 91));

        IReadOnlyList<HidInterfaceInfo> result = WindowsHidEnumerator.FindTargetInterfaces(native);

        Assert.Equal(
            [new HidInterfaceInfo("exact", 0x1532, 0x02E0, 1, 2, 91)],
            result);
        Assert.Equal(1, native.EnumerateCalls);
        Assert.Equal([exact, rejected], native.ReadHandles);
        Assert.All(native.OpenParameters, parameters => Assert.Equal(
            new HidOpenParameters(0, 3, 3, 0, false),
            parameters));
        Assert.Equal(0, native.BindCalls);
        Assert.True(exact.FileHandle.IsClosed);
        Assert.True(rejected.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["exact"]);
        Assert.Equal(1, native.DisposeCounts["rejected"]);
    }

    [Fact]
    public void FindTargetInterfaces_SkipsCandidatesThatCannotBeOpenedOrRead()
    {
        DiagnosticNative native = new();
        native.Add("open-failure", new HidHandleIdentity(0x1532, 0x02E0, 1, 2, 91));
        native.Add("read-failure", new HidHandleIdentity(0x1532, 0x02E0, 1, 2, 91));
        native.OpenFailures.Add("open-failure");
        native.ReadFailures.Add("read-failure");

        IReadOnlyList<HidInterfaceInfo> result = WindowsHidEnumerator.FindTargetInterfaces(native);

        Assert.Empty(result);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void FindCollections_UsesExplicitVidPidWithoutProductionAdmission()
    {
        DiagnosticNative native = new();
        OpenedCandidateHandle first = native.Add(
            "pid02c6-control",
            new HidHandleIdentity(0x1532, 0x02C6, 1, 2, 91));
        OpenedCandidateHandle second = native.Add(
            "pid02c6-consumer",
            new HidHandleIdentity(0x1532, 0x02C6, 12, 1, 375));
        OpenedCandidateHandle production = native.Add(
            "pid02e0",
            new HidHandleIdentity(0x1532, 0x02E0, 1, 2, 91));

        IReadOnlyList<HidInterfaceInfo> result =
            WindowsHidEnumerator.FindCollections(native, 0x1532, 0x02C6);

        Assert.Equal(
            [
                new HidInterfaceInfo("pid02c6-control", 0x1532, 0x02C6, 1, 2, 91),
                new HidInterfaceInfo("pid02c6-consumer", 0x1532, 0x02C6, 12, 1, 375),
            ],
            result);
        Assert.Equal([first, second, production], native.ReadHandles);
        Assert.All(native.OpenParameters, parameters => Assert.Equal(
            new HidOpenParameters(0, 3, 3, 0, false),
            parameters));
        Assert.Equal(0, native.BindCalls);
        Assert.All([first, second, production], handle => Assert.True(handle.FileHandle.IsClosed));
    }

    [Fact]
    public void HyperBoostEligibility_RequiresExactCoolingPadCollection()
    {
        DiagnosticNative absentNative = new();
        var absentSource = new WindowsHyperBoostEligibilitySource(absentNative);

        Assert.Equal(
            CoolingPadConnectionState.Absent,
            absentSource.ObserveCoolingPad());

        DiagnosticNative wrongRevisionNative = new();
        wrongRevisionNative.Add(
            "vid_1532&pid_0f43-wrong-revision",
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0201));

        Assert.Equal(
            CoolingPadConnectionState.ObservationFailed,
            new WindowsHyperBoostEligibilitySource(wrongRevisionNative)
                .ObserveCoolingPad());

        DiagnosticNative exactNative = new();
        exactNative.Add(
            "vid_1532&pid_0f43-exact-pad",
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0200));

        Assert.Equal(
            CoolingPadConnectionState.PresentExact,
            new WindowsHyperBoostEligibilitySource(exactNative)
                .ObserveCoolingPad());
        Assert.Equal(0, exactNative.BindCalls);
    }

    [Fact]
    public void CoolingPadDiscovery_DistinguishesAbsentExactAndAmbiguous()
    {
        DiagnosticNative native = new();
        var source = new WindowsCoolingPadDiscoverySource(native);

        Assert.Equal(CoolingPadConnectionState.Absent, source.Observe());

        native.Add(
            "wrong-revision",
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0201));
        Assert.Equal(CoolingPadConnectionState.Absent, source.Observe());

        native.Add(
            "exact-pad",
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0200));
        Assert.Equal(CoolingPadConnectionState.PresentExact, source.Observe());

        native.Add(
            "second-pad",
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0200));
        Assert.Equal(CoolingPadConnectionState.Ambiguous, source.Observe());
    }

    [Fact]
    public void CoolingPadDiscovery_AllowsExactControlCollectionBesideSiblingCollections()
    {
        DiagnosticNative native = new();
        native.Add(
            @"\\?\hid#vid_1532&pid_0f43&mi_00#control",
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0200));
        native.Add(
            @"\\?\hid#vid_1532&pid_0f43&mi_01#input",
            new HidHandleIdentity(0x1532, 0x0F43, 1, 2, 64, 0x0200));

        Assert.Equal(
            CoolingPadConnectionState.PresentExact,
            new WindowsCoolingPadDiscoverySource(native).Observe());
        Assert.Equal(
            CoolingPadConnectionState.PresentExact,
            new WindowsHyperBoostEligibilitySource(native).ObserveCoolingPad());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoolingPadDiscovery_AllowsUnreadableInputSiblingBesideExactControlCollection(
        bool openFailure)
    {
        DiagnosticNative native = new();
        const string control = @"\\?\hid#vid_1532&pid_0f43&mi_00#control";
        const string sibling = @"\\?\hid#vid_1532&pid_0f43&mi_01#input";
        native.Add(
            control,
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0200));
        native.Add(
            sibling,
            new HidHandleIdentity(0x1532, 0x0F43, 1, 2, 64, 0x0200));
        if (openFailure)
        {
            native.OpenFailures.Add(sibling);
        }
        else
        {
            native.ReadFailures.Add(sibling);
        }

        Assert.Equal(
            CoolingPadConnectionState.PresentExact,
            new WindowsCoolingPadDiscoverySource(native).Observe());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoolingPadDiscovery_TargetMetadataFailureIsNotReportedAsAbsence(
        bool openFailure)
    {
        DiagnosticNative native = new();
        const string targetPath = @"\\?\hid#vid_1532&pid_0f43&mi_00#test";
        native.Add(
            targetPath,
            new HidHandleIdentity(0x1532, 0x0F43, 12, 1, 91, 0x0200));
        if (openFailure)
        {
            native.OpenFailures.Add(targetPath);
        }
        else
        {
            native.ReadFailures.Add(targetPath);
        }

        var source = new WindowsCoolingPadDiscoverySource(native);

        Assert.Equal(CoolingPadConnectionState.ObservationFailed, source.Observe());
    }

    private sealed class DiagnosticNative : IHidCandidateNative
    {
        private readonly Dictionary<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)>
            candidates = [];

        public int EnumerateCalls { get; private set; }

        public int BindCalls { get; private set; }

        public List<HidOpenParameters> OpenParameters { get; } = [];

        public List<OpenedCandidateHandle> ReadHandles { get; } = [];

        public Dictionary<string, int> DisposeCounts { get; } = [];

        public HashSet<string> OpenFailures { get; } = [];

        public HashSet<string> ReadFailures { get; } = [];

        public OpenedCandidateHandle Add(string path, HidHandleIdentity identity)
        {
            SafeFileHandle fileHandle = new(
                new IntPtr(candidates.Count + 200),
                ownsHandle: false);
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
            return candidates.Keys.ToArray();
        }

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            OpenParameters.Add(parameters);
            return OpenFailures.Contains(path)
                ? new(null, 5)
                : new(candidates[path].Handle, null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            ReadHandles.Add(handle);
            KeyValuePair<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)> candidate =
                candidates.Single(pair => ReferenceEquals(pair.Value.Handle, handle));
            if (ReadFailures.Contains(candidate.Key))
            {
                throw new InvalidOperationException("scripted diagnostic read failure");
            }

            return candidate.Value.Identity;
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            BindCalls++;
            throw new InvalidOperationException("Diagnostic enumeration must not bind a handle.");
        }
    }
}
