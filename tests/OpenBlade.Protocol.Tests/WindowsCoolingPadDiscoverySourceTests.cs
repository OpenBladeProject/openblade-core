using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsCoolingPadDiscoverySourceTests
{
    private static readonly HidHandleIdentity Control = new(0x1532, 0x0F43, 0x000C, 1, 91, 0x0200);
    private static readonly HidHandleIdentity ConsumerAuxiliary = new(0x1532, 0x0F43, 0x000C, 1, 0, 0x0101);
    private static readonly HidHandleIdentity SystemAuxiliary = new(0x1532, 0x0F43, 1, 0x80, 0, 0x0101);

    [Fact]
    public void ExactControlAndCapturedZeroFeatureAuxiliariesRemainPresentExact()
    {
        var native = new Native(Control, ConsumerAuxiliary, SystemAuxiliary);
        Assert.Equal(CoolingPadConnectionState.PresentExact, new WindowsCoolingPadDiscoverySource(native).Observe());
        Assert.Equal(3, native.Disposals);
    }

    [Fact]
    public void AuxiliariesAloneCannotAdmitAPadOrEstablishPadlessAbsence()
    {
        var native = new Native(ConsumerAuxiliary, SystemAuxiliary);
        Assert.Equal(CoolingPadConnectionState.ObservationFailed, new WindowsCoolingPadDiscoverySource(native).Observe());
    }

    [Theory]
    [InlineData(0x1532, 0x0F43, 0x000C, 1, 91, 0x0101)]
    [InlineData(0x1532, 0x0F43, 0x000C, 1, 1, 0x0101)]
    [InlineData(0x1532, 0x0F43, 0x000C, 1, 0, 0x0300)]
    [InlineData(0x1532, 0x0F43, 0x0001, 2, 0, 0x0101)]
    [InlineData(0x1533, 0x0F43, 0x000C, 1, 0, 0x0101)]
    [InlineData(0x1532, 0x0F44, 0x000C, 1, 0, 0x0101)]
    public void UnknownOrFeatureCapableWrongIdentityStillFailsClosed(
        ushort vendor, ushort product, ushort page, ushort usage, ushort length, ushort revision)
    {
        var native = new Native(Control, new(vendor, product, page, usage, length, revision));
        Assert.Equal(CoolingPadConnectionState.ObservationFailed, new WindowsCoolingPadDiscoverySource(native).Observe());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnreadableTargetAuxiliaryStillFailsClosed(bool openFailure)
    {
        var native = new Native(Control, ConsumerAuxiliary) { OpenFailure = openFailure, ReadFailure = !openFailure };
        Assert.Equal(CoolingPadConnectionState.ObservationFailed, new WindowsCoolingPadDiscoverySource(native).Observe());
    }

    [Fact]
    public void MultipleExactControlsRemainAmbiguous()
    {
        var native = new Native(Control, ConsumerAuxiliary, Control);
        Assert.Equal(CoolingPadConnectionState.Ambiguous, new WindowsCoolingPadDiscoverySource(native).Observe());
    }

    private sealed class Native(params HidHandleIdentity[] identities) : IHidCandidateNative
    {
        private readonly Dictionary<OpenedCandidateHandle, int> handles = [];
        internal bool OpenFailure;
        internal bool ReadFailure;
        internal int Disposals;
        public IReadOnlyList<string> EnumerateDevicePaths() => Enumerable.Range(0, identities.Length)
            .Select(index => $"vid_1532&pid_0f43&mi_00#{index}").ToArray();

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            Assert.Equal(0u, parameters.DesiredAccess);
            int index = int.Parse(path[(path.LastIndexOf('#') + 1)..]);
            if (index == 1 && OpenFailure)
            {
                return new(null, 5);
            }
            var file = new SafeFileHandle(new IntPtr(index + 100), ownsHandle: false);
            var handle = new OpenedCandidateHandle(file, () => { Disposals++; file.Dispose(); });
            handles.Add(handle, index);
            return new(handle, null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            int index = handles[handle];
            if (index == 1 && ReadFailure)
            {
                throw new IOException("Metadata unavailable");
            }
            return identities[index];
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) =>
            throw new InvalidOperationException("Discovery must never bind a firmware control handle");
    }
}
