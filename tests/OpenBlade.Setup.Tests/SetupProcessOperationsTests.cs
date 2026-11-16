using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup.Tests;

public sealed class SetupProcessOperationsTests
{
    [Fact]
    public async Task NativeExitWait_ObservesSignalFromOwnedDuplicate()
    {
        // An inert event exercises the Windows wait/duplication ABI without
        // inspecting or launching any process.
        using EventWaitHandle signal = new(false, EventResetMode.ManualReset);
        using SafeProcessHandle source = new(signal.SafeWaitHandle.DangerousGetHandle(), ownsHandle: false);
        Task wait = WindowsSetupProcessNativeOperations.Instance.WaitForExitAsync(source, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        source.Dispose();
        signal.Set();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NativeExitWait_CancellationRemainsSafeAfterSourceHandlesClose()
    {
        using EventWaitHandle signal = new(false, EventResetMode.ManualReset);
        using SafeProcessHandle source = new(signal.SafeWaitHandle.DangerousGetHandle(), ownsHandle: false);
        using CancellationTokenSource cancellation = new();
        Task wait = WindowsSetupProcessNativeOperations.Instance.WaitForExitAsync(source, cancellation.Token);
        source.Dispose();
        signal.Dispose();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task IdentityAndExit_UseOneLeastPrivilegeRetainedObject()
    {
        NativeOperations native = new();
        using RetainedSetupProcess process = new(123, native);

        // Tray/guardian callers read the image without a prior retain call.
        Assert.Equal(@"C:\Installed\OpenBlade.Service.exe", process.ExecutablePath);
        Assert.True(process.TryRetainLifetime());
        await process.WaitForExitAsync(CancellationToken.None);
        native.Exited = true;
        Assert.False(process.TryRetainLifetime());
        Assert.Null(process.ExecutablePath);
        await process.WaitForExitAsync(CancellationToken.None);
        Assert.Equal(1, native.Opens);
        Assert.Equal(1, native.Waits);
        Assert.Equal(0x00101000u, native.RequestedAccess);
        Assert.False(native.Handle.IsClosed);
        process.Dispose();
        Assert.True(native.Handle.IsClosed);
    }

    [Fact]
    public void ProcessGoneBeforePin_IsNotReopenedForReusedPid()
    {
        NativeOperations native = new() { GoneOnOpen = true };
        using RetainedSetupProcess process = new(123, native);
        Assert.False(process.TryRetainLifetime());
        native.GoneOnOpen = false; // A replacement could now occupy the PID.
        Assert.Null(process.ExecutablePath);
        Assert.False(process.TryRetainLifetime());
        Assert.Equal(1, native.Opens);
    }

    [Fact]
    public void AccessDenied_DoesNotBecomeConfirmedExit()
    {
        NativeOperations native = new() { OpenFailure = new Win32Exception(5) };
        using RetainedSetupProcess process = new(123, native);
        Assert.Throws<Win32Exception>(() => process.TryRetainLifetime());
        Assert.Throws<Win32Exception>(() => process.ExecutablePath);
        Assert.Equal(2, native.Opens);
        Assert.Equal(0, native.Waits);
    }

    [Fact]
    public async Task CancellationAndImageFailure_KeepTheRetainedIdentityUntilDispose()
    {
        NativeOperations native = new();
        using RetainedSetupProcess process = new(123, native);
        Assert.True(process.TryRetainLifetime());
        native.ImageFailure = new Win32Exception(5);
        Assert.Throws<Win32Exception>(() => process.ExecutablePath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitForExitAsync(cancellation.Token));
        Assert.Equal(1, native.Opens);
        Assert.False(native.Handle.IsClosed);
    }

    private sealed class NativeOperations : ISetupProcessNativeOperations
    {
        internal SafeProcessHandle Handle { get; } = new(new nint(12345), ownsHandle: false);
        internal int Opens { get; private set; }
        internal uint RequestedAccess { get; private set; }
        internal int Waits { get; private set; }
        internal bool Exited { get; set; }
        internal bool GoneOnOpen { get; set; }
        internal Exception? OpenFailure { get; init; }
        internal Exception? ImageFailure { get; set; }
        public SafeProcessHandle? Open(int processId, uint desiredAccess)
        {
            Assert.Equal(123, processId);
            Opens++;
            RequestedAccess = desiredAccess;
            if (OpenFailure is not null) { throw OpenFailure; }
            return GoneOnOpen ? null : Handle;
        }
        public bool HasExited(SafeProcessHandle handle) { Assert.Same(Handle, handle); return Exited; }
        public string ExecutablePath(SafeProcessHandle handle)
        {
            Assert.Same(Handle, handle);
            if (ImageFailure is not null) { throw ImageFailure; }
            return @"C:\Installed\OpenBlade.Service.exe";
        }
        public Task WaitForExitAsync(SafeProcessHandle handle, CancellationToken cancellationToken)
        {
            Assert.Same(Handle, handle);
            cancellationToken.ThrowIfCancellationRequested();
            Waits++;
            return Task.CompletedTask;
        }
    }
}
