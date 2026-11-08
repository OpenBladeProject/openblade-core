using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsCoolingPadHidSessionOpenerTests
{
    private const string PadPath = @"\\?\hid#vid_1532&pid_0f43&mi_00#pad";
    private const string OtherPadPath = @"\\?\hid#vid_1532&pid_0f43&mi_00#other";

    [Fact]
    public async Task ExactAccessoryOpensWithoutAnyLaptopIdentityAndTransfersHandleOwnership()
    {
        var native = new Native();
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        var result = opener.Open(new Guard(() => new([], null)));
        Assert.Null(result.Failure);
        var handle = Assert.IsType<OpenedHidHandle>(result.Value);
        Assert.Equal(PadPath, opener.SelectedPath);
        Assert.Equal(1, native.Opened);
        Assert.Equal(0, native.Closed);
        await handle.DisposeAsync();
        await handle.DisposeAsync();
        Assert.Equal(native.Opened, native.Closed);
        Assert.Equal(1, native.BoundClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryAccessoryIdentityFieldIsRequired(int field)
    {
        var native = new Native();
        native.Identity = field switch
        {
            0 => native.Identity with { VendorId = 0x1234 },
            1 => native.Identity with { ProductId = 0x02E0 },
            2 => native.Identity with { UsagePage = 1 },
            3 => native.Identity with { Usage = 2 },
            4 => native.Identity with { FeatureReportByteLength = 90 },
            _ => native.Identity with { VersionNumber = 0x0100 },
        };
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, opener.Open(Allow()).Failure);
        Assert.False(opener.StillExact());
        Assert.Equal(native.Opened, native.Closed);
        Assert.Equal(0, native.Bound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrDuplicateAccessoriesNeverBind(bool duplicate)
    {
        var native = new Native { Paths = duplicate ? [PadPath, OtherPadPath] : [] };
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, opener.Open(Allow()).Failure);
        Assert.False(opener.StillExact());
        Assert.Equal(native.Opened, native.Closed);
        Assert.Equal(0, native.Bound);
    }

    [Fact]
    public void OtherInterfacesAndProductsCannotSupplyTheAccessoryHandle()
    {
        var native = new Native { Paths = [PadPath.Replace("mi_00", "mi_01"), PadPath.Replace("0f43", "02e0")] };
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, opener.Open(Allow()).Failure);
        Assert.False(opener.StillExact());
        Assert.Equal(0, native.Opened);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnreadableCandidateClosesEarlierCandidatesAndDoesNotBind(bool openFails)
    {
        var native = new Native
        {
            Paths = [PadPath, OtherPadPath],
            OpenErrorPath = openFails ? OtherPadPath : null,
            IdentityErrorPath = openFails ? null : OtherPadPath,
        };
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        Assert.NotNull(opener.Open(Allow()).Failure);
        Assert.Equal(native.Opened, native.Closed);
        Assert.Equal(0, native.Bound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedControllerAdmissionDoesNotOpenAnyCandidate(bool detectionFailure)
    {
        var native = new Native();
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        var result = opener.Open(new Guard(() => detectionFailure ? new([], "unreadable") : new(["Synapse"], null)));
        Assert.Equal(detectionFailure ? HidSessionFailureKind.ConflictDetectionFailed : HidSessionFailureKind.ConflictBlocked, result.Failure);
        Assert.Equal(0, native.Opened);
    }

    [Fact]
    public async Task AccessDeniedFallbackIsRestrictedToOpeningBeforeFirmwareIo()
    {
        var native = new Native { OperationalOpenError = 5 };
        var result = new WindowsCoolingPadHidSessionOpener(native).Open(Allow());
        Assert.Null(result.Failure);
        Assert.Equal(new uint[] { 0xC0000000, 0 }, native.Accesses);
        await result.Value!.DisposeAsync();
        Assert.Equal(native.Opened, native.Closed);

        var failed = new Native { OperationalOpenError = 32 };
        Assert.NotNull(new WindowsCoolingPadHidSessionOpener(failed).Open(Allow()).Failure);
        Assert.Equal(new uint[] { 0xC0000000 }, failed.Accesses);
    }

    [Fact]
    public void BindingFailureClosesTheCandidate()
    {
        var native = new Native { BindFails = true };
        Assert.Equal(HidSessionFailureKind.NativeIoFailure,
            new WindowsCoolingPadHidSessionOpener(native).Open(Allow()).Failure);
        Assert.Equal(native.Opened, native.Closed);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("geometry")]
    [InlineData("revision")]
    [InlineData("duplicate")]
    [InlineData("replacement")]
    [InlineData("unreadable")]
    [InlineData("controller")]
    public async Task NativeSetRequiresFreshAccessoryAndControllerAdmission(string change)
    {
        var native = new Native();
        var opener = new WindowsCoolingPadHidSessionOpener(native);
        var processes = new Processes();
        var guard = new WindowsCoolingPadDeviceProvider.CoolingPadAccessGuard(processes, new Services(), opener.StillExact);
        var io = new Io();
        var factory = new ValidationActorFactory(() => opener.Open(guard),
            (handle, lease) => new WindowsRazerHidSession(handle, io, guard, TimeProvider.System, lease,
                ControllerExclusivityPolicy.Strict, featureReportId: 0), TimeSpan.FromSeconds(1));
        var opened = await factory.OpenAsync(new ValidationRunContext());
        Assert.Null(opened.Failure);
        await using var actor = Assert.IsType<ValidationActor>(opened.Value);
        Assert.Empty(io.Reports);
        switch (change)
        {
            case "geometry": native.Identity = native.Identity with { FeatureReportByteLength = 90 }; break;
            case "revision": native.Identity = native.Identity with { VersionNumber = 0x0100 }; break;
            case "duplicate": native.Paths = [PadPath, OtherPadPath]; break;
            case "replacement": native.Paths = [OtherPadPath]; break;
            case "unreadable": native.IdentityErrorPath = PadPath; break;
            case "controller": processes.Names = ["FanControl"]; break;
        }
        var result = await ((IRazerSetFeatureCommandSession)actor.Session).SubmitAsync(
            CoolingPadProtocol.FrameSet(Enumerable.Repeat(new CoolingPadColor(1, 2, 3), 18).ToArray()),
            () => TransactionIdAssignment.Valid(1), TimeSpan.FromSeconds(1), default);
        if (change == "ready")
        {
            Assert.Null(result.Failure);
            byte[] report = Assert.Single(io.Reports);
            Assert.Equal(91, report.Length);
            Assert.Equal(0, report[0]);
        }
        else
        {
            Assert.NotNull(result.Failure);
            Assert.Empty(io.Reports);
        }
        await actor.DisposeAsync();
        Assert.Equal(native.Opened, native.Closed);
        Assert.Equal(1, native.BoundClosed);
    }

    private static Guard Allow() => new(() => new([], null));
    private sealed class Guard(Func<HardwareAccessDecision> check) : IHardwareAccessGuard
    { public HardwareAccessDecision Check() => check(); }
    private sealed class Processes : IRunningProcessSource
    {
        internal string[] Names { get; set; } = [];
        public IReadOnlyList<string> GetProcessNames() => Names;
    }
    private sealed class Services : IWindowsServiceSource
    { public IReadOnlyList<WindowsServiceSnapshot> GetServices() => []; }
    private sealed class Io : IHidFeatureIo
    {
        internal List<byte[]> Reports { get; } = [];
        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint controlCode, byte[] buffer,
            CancellationToken callerCancellation, CancellationToken deadlineCancellation)
        {
            Assert.Equal(0x000B0191u, controlCode);
            Reports.Add(buffer.ToArray());
            return Task.FromResult(new HidFeatureIoResult(new NativeIoCompletion(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed));
        }
    }
    private sealed class Native : IHidCandidateNative
    {
        internal string[] Paths { get; set; } = [PadPath];
        internal HidHandleIdentity Identity { get; set; } = WindowsCoolingPadDeviceProvider.ExactIdentity;
        internal string? OpenErrorPath { get; set; }
        internal string? IdentityErrorPath { get; set; }
        internal int? OperationalOpenError { get; set; }
        internal bool BindFails { get; set; }
        internal int Opened { get; private set; }
        internal int Closed { get; private set; }
        internal int Bound { get; private set; }
        internal int BoundClosed { get; private set; }
        internal List<uint> Accesses { get; } = [];
        private readonly Dictionary<OpenedCandidateHandle, string> paths = [];
        public IReadOnlyList<string> EnumerateDevicePaths() => Paths;
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            Accesses.Add(parameters.DesiredAccess);
            if (path == OpenErrorPath) { return new(null, 32); }
            if (parameters.DesiredAccess != 0 && OperationalOpenError is { } error) { return new(null, error); }
            var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
            var handle = new OpenedCandidateHandle(file, () => Closed++);
            paths.Add(handle, path);
            Opened++;
            return new(handle, null);
        }
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => paths[handle] == IdentityErrorPath
            ? throw new IOException("Unreadable identity") : Identity;
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            if (BindFails) { throw new IOException("Bind failed"); }
            var file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            Bound++;
            return new(file, bound, () => BoundClosed++, () => Closed++);
        }
    }
}
