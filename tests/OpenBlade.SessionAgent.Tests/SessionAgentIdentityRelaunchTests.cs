using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Platform;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentIdentityRelaunchTests
{
    [Fact]
    public async Task RelaunchReleasesBothPipeServersBeforeStartingReplacement()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User!;
        var displayEndpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.RelaunchTests.{Guid.NewGuid():N}");
        var gpuEndpoint = new SessionAgentPipeEndpoint(
            $"{SessionGpuIpc.PipeNamePrefix}.RelaunchTests.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(
            displayEndpoint,
            sid,
            out SessionAgentPipeOwner? displayOwner));
        Assert.True(SessionAgentPipeOwner.TryAcquire(
            gpuEndpoint,
            sid,
            out SessionAgentPipeOwner? gpuOwner));
        await using SessionAgentPipeOwner acquiredDisplayOwner = displayOwner!;
        await using SessionAgentPipeOwner acquiredGpuOwner = gpuOwner!;
        await using var displayServer = new SessionDisplayPipeServer(
            acquiredDisplayOwner,
            new SessionDisplayRequestDispatcher(new UnusedDisplayProfileApplier()));
        await using var gpuServer = new SessionGpuPipeServer(
            acquiredGpuOwner,
            new SessionGpuRequestDispatcher(new UnusedGpuAppManager()));

        int result = await Program.RelaunchAfterIdentityRegistrationAsync(
            displayServer,
            gpuServer,
            () => AcquireReplacementPair(displayEndpoint, gpuEndpoint, sid));

        Assert.Equal(17, result);
    }

    private static int AcquireReplacementPair(
        SessionAgentPipeEndpoint displayEndpoint,
        SessionAgentPipeEndpoint gpuEndpoint,
        SecurityIdentifier sid)
    {
        SessionAgentPipeOwner? displayReplacement = null;
        SessionAgentPipeOwner? gpuReplacement = null;
        try
        {
            Assert.True(SessionAgentPipeOwner.TryAcquire(
                displayEndpoint,
                sid,
                out displayReplacement));
            Assert.True(SessionAgentPipeOwner.TryAcquire(
                gpuEndpoint,
                sid,
                out gpuReplacement));
            return 17;
        }
        finally
        {
            gpuReplacement?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            displayReplacement?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class UnusedDisplayProfileApplier : IDisplayProfileApplier
    {
        public Task<SessionDisplayExecutionResult> QueryAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedGpuAppManager : IWindowsGpuAppManager
    {
        public WindowsGpuAppScan Scan(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<WindowsGpuAppActionResult> CloseGracefully(
            IReadOnlyList<WindowsGpuApp> apps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IReadOnlyList<WindowsGpuAppActionResult> ForceClose(
            IReadOnlyList<WindowsGpuApp> apps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
