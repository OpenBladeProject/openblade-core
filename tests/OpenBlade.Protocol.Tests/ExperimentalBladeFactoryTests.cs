using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeFactoryTests
{
    [Fact]
    public async Task CancellationAfterSelectingFirstInterface_ReturnsCanceledAndDisposesBothHandles()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native(2) { BeforeSecondInterfaceRecheck = cancellation.Cancel };
        var io = new Io();
        var factory = Create(native, io);

        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(cancellation.Token);

        Assert.Null(result.Value);
        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Equal(2, native.BoundCount);
        Assert.All(native.DisposeCounts, count => Assert.Equal(1, count));
        Assert.Equal(2, io.Commands.Count);
        Assert.All(io.Commands, command => Assert.Equal((byte)0x82, command));
    }

    [Fact]
    public async Task CancellationDuringFunctionalProbe_ReturnsCanceledAndDisposesHandle()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native(1);
        var io = new Io { AfterGet = cancellation.Cancel };
        var factory = Create(native, io);

        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(cancellation.Token);

        Assert.Null(result.Value);
        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.Equal(1, native.BoundCount);
        Assert.Equal(1, native.DisposeCounts[0]);
        Assert.Single(io.Commands);
        Assert.Equal((byte)0x82, io.Commands[0]);
    }

    private static ExperimentalBladeFactory Create(Native native, Io io) =>
        new(ExperimentalBladeModels.All[0], new Identity(), native, new Guard(), io,
            () => PowerSource.ExternalUnknown);

    private sealed class Identity : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity() => new(
            new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0483", null, "test-firmware"), null);
    }

    private sealed class Guard : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], null);
    }

    private sealed class Native : IHidCandidateNative
    {
        private readonly OpenedCandidateHandle[] handles;
        private readonly int[] reads;
        internal int[] DisposeCounts { get; }
        internal int BoundCount { get; private set; }
        internal Action? BeforeSecondInterfaceRecheck { get; init; }

        internal Native(int count)
        {
            DisposeCounts = new int[count];
            reads = new int[count];
            handles = Enumerable.Range(0, count).Select(index =>
            {
                var file = new SafeFileHandle(new IntPtr(500 + index), ownsHandle: false);
                return new OpenedCandidateHandle(file, () =>
                {
                    DisposeCounts[index]++;
                    file.Dispose();
                });
            }).ToArray();
        }

        public IReadOnlyList<string> EnumerateDevicePaths() =>
            Enumerable.Range(0, handles.Length).Select(index => index.ToString()).ToArray();

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(handles[int.Parse(path)], null);

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            int index = Array.IndexOf(handles, handle);
            if (++reads[index] == 2 && index == 1)
            {
                BeforeSecondInterfaceRecheck?.Invoke();
            }
            return new(0x1532, 0x029F, 1, 2, 91);
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            int index = Array.IndexOf(handles, handle);
            BoundCount++;
            SafeFileHandle file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            return new(file, bound, () => { }, () =>
            {
                DisposeCounts[index]++;
                file.Dispose();
            });
        }
    }

    private sealed class Io : IHidFeatureIo
    {
        private byte[] lastRequest = [];
        internal List<byte> Commands { get; } = [];
        internal Action? AfterGet { get; init; }

        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint controlCode,
            byte[] buffer, CancellationToken callerCancellation, CancellationToken deadlineCancellation)
        {
            if (controlCode == 0x000B0191)
            {
                lastRequest = buffer.ToArray();
                Commands.Add(buffer[8]);
            }
            else
            {
                lastRequest.CopyTo(buffer, 0);
                buffer[1] = 0x02;
                AfterGet?.Invoke();
            }
            return Task.FromResult(new HidFeatureIoResult(new(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed));
        }
    }
}
