using Microsoft.Win32.SafeHandles;
using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

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
