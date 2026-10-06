using System.ComponentModel;
using System.Security.Cryptography;

namespace OpenBlade.DeploymentClient;

internal interface IRequestIdSource
{
    Guid Create();
}

internal sealed class CryptographicRequestIdSource : IRequestIdSource
{
    public Guid Create()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }
}

internal sealed class DeploymentClientCoordinator(
    IBrokerChannel broker,
    IClientIntegritySource integrity,
    IClientSessionSource session,
    ILegacyStartupCleanup legacyStartup,
    IInstalledPayloadVerifier installedPayload,
    IInstalledProcessInventory inventory,
    IInstalledProcessShutdown shutdown,
    IDeploymentProcessLauncher processes,
    IRequestIdSource requestIds,
    Action<string>? diagnostic = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private static readonly TimeSpan SessionAgentSurvivalWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FinalInventoryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinalInventoryPollInterval = TimeSpan.FromMilliseconds(100);
    private const string ExactRollbackCode = "install-rolled-back";
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    internal async Task<bool> ApplyFixedInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!HasRequiredContext())
            {
                return Fail("client-context-rejected");
            }

            // Retain a verified desktop lifetime parent before stopping installed
            // processes or asking the broker to change their payload.
            using IDeploymentLaunchContext? launchContext = TryPrepareLaunchContext();
            if (launchContext is null)
            {
                return false;
            }

            InstalledPayloadPaths? expectedPaths = installedPayload.GetExpectedPaths();
            if (expectedPaths is null)
            {
                return Fail("installed-paths-unavailable");
            }

            InstalledProcessInventory initialInventory = inventory.Inspect(expectedPaths);
            if (!initialInventory.TryGetRestorableShape(out InstalledProcessShape originalShape))
            {
                return Fail("installed-process-state-rejected");
            }

            if (originalShape != InstalledProcessShape.None)
            {
                InstalledPayloadPaths? verifiedBeforeShutdown =
                    installedPayload.GetVerifiedPaths();
                if (verifiedBeforeShutdown is null
                    || !InstalledPayloadPathPolicy.IsExact(
                        verifiedBeforeShutdown,
                        expectedPaths))
                {
                    return Fail("installed-payload-unverified-before-shutdown");
                }

                if (!ValidateLaunchContext(launchContext))
                {
                    return false;
                }

                bool stopped;
                try
                {
                    stopped = await shutdown.ShutdownAsync(
                        verifiedBeforeShutdown,
                        originalShape,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException or InvalidDataException
                    or TimeoutException or Win32Exception or InvalidOperationException)
                {
                    return Fail("installed-process-shutdown-failed");
                }

                if (!stopped)
                {
                    return Fail("installed-process-shutdown-failed");
                }
            }

            if (!HasRequiredContext())
            {
                return Fail("client-context-changed-before-broker");
            }

            if (!inventory.Inspect(expectedPaths).IsEmpty)
            {
                return Fail("installed-process-shutdown-incomplete");
            }

            if (!ValidateLaunchContext(launchContext))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Guid requestId = requestIds.Create();
            if (requestId == Guid.Empty)
            {
                throw new InvalidOperationException("The deployment client request ID is unavailable.");
            }

            DeploymentBrokerResponse response = await broker.ApplyFixedInboxAsync(
                requestId,
                cancellationToken).ConfigureAwait(false);
            if (DeploymentBrokerProtocol.IsExactSuccess(response, requestId))
            {
                return await CompleteSuccessfulInstallAsync(
                    expectedPaths,
                    launchContext,
                    cancellationToken).ConfigureAwait(false);
            }

            if (IsExactRollback(response, requestId))
            {
                diagnostic?.Invoke($"broker-response:{response.Status}:{response.Code}");
                await RestoreAfterRollbackAsync(
                    expectedPaths,
                    originalShape,
                    launchContext,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            return Fail($"broker-response:{response.Status}:{response.Code}");
        }
        catch (OperationCanceledException)
        {
            return Fail("client-operation-canceled");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or TimeoutException or Win32Exception or InvalidOperationException)
        {
            return Fail($"client-exception:{exception.GetType().Name}");
        }
    }

    private IDeploymentLaunchContext? TryPrepareLaunchContext()
    {
        try
        {
            return processes.Prepare();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Fail("desktop-launch-context-rejected");
            return null;
        }
    }

    private bool ValidateLaunchContext(IDeploymentLaunchContext context)
    {
        try
        {
            context.Validate();
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return Fail("desktop-launch-context-changed");
        }
    }

    private async Task<bool> CompleteSuccessfulInstallAsync(
        InstalledPayloadPaths expectedPaths,
        IDeploymentLaunchContext launchContext,
        CancellationToken cancellationToken)
    {
        if (!HasRequiredContext())
        {
            return Fail("client-context-changed");
        }

        InstalledPayloadPaths? paths = installedPayload.GetVerifiedPaths();
        if (paths is null || !InstalledPayloadPathPolicy.IsExact(paths, expectedPaths))
        {
            return Fail("installed-payload-unverified");
        }

        if (!inventory.Inspect(paths).IsEmpty)
        {
            return Fail("installed-processes-already-running");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            legacyStartup.RemoveCurrentUserEntries();
        }
        catch (Exception)
        {
            return Fail("legacy-startup-cleanup-failed");
        }

        return await LaunchShapeAsync(
            paths,
            InstalledProcessShape.SessionAgentAndTray,
            launchContext,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreAfterRollbackAsync(
        InstalledPayloadPaths expectedPaths,
        InstalledProcessShape originalShape,
        IDeploymentLaunchContext launchContext,
        CancellationToken cancellationToken)
    {
        if (!HasRequiredContext())
        {
            return;
        }

        InstalledPayloadPaths? paths = installedPayload.GetVerifiedPaths();
        if (paths is null
            || !InstalledPayloadPathPolicy.IsExact(paths, expectedPaths)
            || !inventory.Inspect(paths).IsEmpty)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await LaunchShapeAsync(paths, originalShape, launchContext, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> LaunchShapeAsync(
        InstalledPayloadPaths paths,
        InstalledProcessShape shape,
        IDeploymentLaunchContext launchContext,
        CancellationToken cancellationToken)
    {
        if (shape == InstalledProcessShape.None)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidateLaunchContext(launchContext))
        {
            return false;
        }

        using ILaunchedDeploymentProcess? sessionAgent = launchContext.Launch(
            InstalledProcessLaunchPlan.SessionAgent(paths));
        if (sessionAgent is null
            || !await sessionAgent.RemainsAliveAsync(SessionAgentSurvivalWindow)
                .ConfigureAwait(false)
            || !inventory.Inspect(paths).HasOnlySessionAgent)
        {
            return Fail("session-agent-relaunch-failed");
        }

        if (shape == InstalledProcessShape.SessionAgentOnly)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidateLaunchContext(launchContext))
        {
            return false;
        }

        using ILaunchedDeploymentProcess? tray = launchContext.Launch(
            InstalledProcessLaunchPlan.Tray(paths));
        if (tray is null
            || !await tray.RemainsAliveAsync(SessionAgentSurvivalWindow)
                .ConfigureAwait(false))
        {
            return Fail("tray-relaunch-failed");
        }

        string? failure = await WaitForFinalInventoryAsync(
            paths, sessionAgent, tray, cancellationToken).ConfigureAwait(false);
        return failure is null || Fail(failure);
    }

    private async Task<string?> WaitForFinalInventoryAsync(
        InstalledPayloadPaths paths,
        ILaunchedDeploymentProcess sessionAgent,
        ILaunchedDeploymentProcess tray,
        CancellationToken cancellationToken)
    {
        long started = timeProvider.GetTimestamp();
        bool previousInventoryExact = false;
        while (timeProvider.GetElapsedTime(started) < FinalInventoryTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? failure = GetFinalInventoryAdmissionFailure(sessionAgent, tray);
            if (failure is not null)
            {
                return failure;
            }

            // Tray startup can briefly create a duplicate Session Agent. Only
            // observations are retried; no launch or broker request is repeated.
            bool inventoryExact = inventory.Inspect(paths).HasExactFinalState;
            cancellationToken.ThrowIfCancellationRequested();
            failure = GetFinalInventoryAdmissionFailure(sessionAgent, tray);
            if (failure is not null)
            {
                return failure;
            }
            if (timeProvider.GetElapsedTime(started) >= FinalInventoryTimeout)
            {
                return "final-inventory-timeout";
            }

            if (inventoryExact && previousInventoryExact)
            {
                return null;
            }
            previousInventoryExact = inventoryExact;

            TimeSpan remaining = FinalInventoryTimeout - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return "final-inventory-timeout";
            }
            await delay(
                remaining < FinalInventoryPollInterval ? remaining : FinalInventoryPollInterval,
                cancellationToken).ConfigureAwait(false);
        }
        return "final-inventory-timeout";
    }

    private string? GetFinalInventoryAdmissionFailure(
        ILaunchedDeploymentProcess sessionAgent,
        ILaunchedDeploymentProcess tray)
    {
        if (!HasRequiredContext())
        {
            return "client-context-changed";
        }
        if (sessionAgent.HasExited)
        {
            return "session-agent-relaunch-failed";
        }
        return tray.HasExited ? "tray-relaunch-failed" : null;
    }

    private bool HasRequiredContext() =>
        !integrity.IsElevated() && session.IsActiveLocalConsoleSession();

    private static bool IsExactRollback(
        DeploymentBrokerResponse response,
        Guid requestId) =>
        response.ProtocolVersion == DeploymentBrokerProtocol.ProtocolVersion
        && response.RequestId == requestId
        && response.Status == DeploymentBrokerResponseStatus.RolledBack
        && string.Equals(response.Code, ExactRollbackCode, StringComparison.Ordinal);

    private bool Fail(string code)
    {
        diagnostic?.Invoke(code);
        return false;
    }
}
