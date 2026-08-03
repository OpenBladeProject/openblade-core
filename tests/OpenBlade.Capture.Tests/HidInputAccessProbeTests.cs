using Microsoft.Win32.SafeHandles;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class HidInputAccessProbeTests
{
    [Fact]
    public void Run_ReportsSanitizedExactCollectionReadAccess()
    {
        var native = new ScriptedNative(
            new Dictionary<string, HidHandleIdentity>
            {
                [@"\\?\HID#VID_1532&PID_02C6&MI_01#UNIQUE-A"] =
                    new(0x1532, 0x02C6, 0x0001, 0x0006, 0),
                [@"\\?\HID#VID_1532&PID_02C6&MI_01#UNIQUE-B"] =
                    new(0x1532, 0x02C6, 0x000C, 0x0001, 0),
                [@"\\?\HID#VID_1532&PID_02E0&MI_01#OTHER"] =
                    new(0x1532, 0x02E0, 0x0001, 0x0006, 0),
            });
        native.ReadOpenErrors[
            @"\\?\HID#VID_1532&PID_02C6&MI_01#UNIQUE-A"] = 5;

        IReadOnlyList<HidInputAccessObservation> result =
            new HidInputAccessProbe(native).Run();

        Assert.Equal(2, result.Count);
        Assert.Equal(
            new HidInputAccessObservation(
                "MI_01",
                0x0001,
                0x0006,
                0,
                ReadHandleOpened: false,
                IdentityReverified: false,
                Win32Error: 5,
                Failure: null),
            result[0]);
        Assert.Equal(
            new HidInputAccessObservation(
                "MI_01",
                0x000C,
                0x0001,
                0,
                ReadHandleOpened: true,
                IdentityReverified: true,
                Win32Error: null,
                Failure: null),
            result[1]);
        Assert.Equal(3, native.MetadataOpenCount);
        Assert.Equal(2, native.ReadOpenCount);
        Assert.All(
            native.ReadOpenParameters,
            parameters => Assert.Equal(
                new HidOpenParameters(
                    0x80000000u,
                    0x00000003u,
                    3u,
                    0x40000000u,
                    false),
                parameters));
    }

    [Fact]
    public void Run_FailsClosedWhenOpenOrIdentityReadThrows()
    {
        var native = new ScriptedNative(
            new Dictionary<string, HidHandleIdentity>
            {
                ["open-throws"] = new(0x1532, 0x02C6, 1, 6, 0),
                ["identity-throws"] = new(0x1532, 0x02C6, 12, 1, 0),
            });
        native.ReadOpenThrows.Add("open-throws");
        native.ReadIdentityThrowsOnReadOpen.Add("identity-throws");

        IReadOnlyList<HidInputAccessObservation> result =
            new HidInputAccessProbe(native).Run();

        Assert.Equal("OpenFailed", result[0].Failure);
        Assert.False(result[0].ReadHandleOpened);
        Assert.Equal("IdentityReadFailed", result[1].Failure);
        Assert.True(result[1].ReadHandleOpened);
        Assert.False(result[1].IdentityReverified);
    }

    private sealed class ScriptedNative : IHidCandidateNative
    {
        private readonly IReadOnlyDictionary<string, HidHandleIdentity> identities;
        private readonly Dictionary<OpenedCandidateHandle, (string Path, bool ReadOpen)> handles =
            [];
        private int handleValue = 500;

        internal ScriptedNative(
            IReadOnlyDictionary<string, HidHandleIdentity> identities)
        {
            this.identities = identities;
        }

        internal Dictionary<string, int> ReadOpenErrors { get; } = [];

        internal HashSet<string> ReadOpenThrows { get; } = [];

        internal HashSet<string> ReadIdentityThrowsOnReadOpen { get; } = [];

        internal int MetadataOpenCount { get; private set; }

        internal int ReadOpenCount { get; private set; }

        internal List<HidOpenParameters> ReadOpenParameters { get; } = [];

        public IReadOnlyList<string> EnumerateDevicePaths() => identities.Keys.ToArray();

        public HidCandidateOpenResult TryOpen(
            string path,
            HidOpenParameters parameters)
        {
            bool readOpen = parameters.DesiredAccess != 0;
            if (!readOpen)
            {
                MetadataOpenCount++;
            }
            else
            {
                ReadOpenCount++;
                ReadOpenParameters.Add(parameters);
                if (ReadOpenThrows.Contains(path))
                {
                    throw new InvalidOperationException("scripted open failure");
                }
                if (ReadOpenErrors.TryGetValue(path, out int error))
                {
                    return new(null, error);
                }
            }

            SafeFileHandle fileHandle =
                new(new IntPtr(handleValue++), ownsHandle: false);
            var candidate = new OpenedCandidateHandle(
                fileHandle,
                fileHandle.Dispose);
            handles.Add(candidate, (path, readOpen));
            return new(candidate, null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            (string path, bool readOpen) = handles[handle];
            if (readOpen && ReadIdentityThrowsOnReadOpen.Contains(path))
            {
                throw new InvalidOperationException("scripted identity failure");
            }

            return identities[path];
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) =>
            throw new InvalidOperationException("The access probe must not bind handles.");
    }
}
