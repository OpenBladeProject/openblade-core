using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup;

internal sealed record SetupFixtureProcessHandoff(
    SafeProcessHandle OriginalHandle, uint ProcessId, long CreationFileTimeUtc);

internal sealed class WindowsSetupRetainedFixtureLaunch : ISetupRetainedCandidateLaunch, IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly WindowsSetupServiceImagePin image;
    private readonly WindowsSetupRetainedCandidateProcess process;
    private bool evidenceLost;

    private WindowsSetupRetainedFixtureLaunch(
        WindowsSetupServiceImagePin image, WindowsSetupRetainedCandidateProcess process, SetupCandidateLaunch expected)
    {
        this.image = image;
        this.process = process;
        Expected = expected;
    }

    internal SetupCandidateLaunch Expected { get; }

    // The trusted fixture launcher starts exactly the supplied path and returns
    // that launch's original handle. It owns child cleanup on every outcome.
    // Process image names alone cannot bind a process to the prelaunch pin.
    internal static WindowsSetupRetainedFixtureLaunch Launch(
        SetupFixtureServiceBuild expected, Func<string, CancellationToken, SetupFixtureProcessHandoff> launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var image = WindowsSetupServiceImagePin.OpenSyntheticFixture(expected, cancellationToken);
        WindowsSetupRetainedCandidateProcess? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetupFixtureProcessHandoff handoff = launch(image.ExecutablePath, cancellationToken);
            var identity = new SetupCandidateProcessIdentity(handoff.ProcessId, handoff.CreationFileTimeUtc,
                image.ExecutablePath);
            process = WindowsSetupRetainedCandidateProcess.TryCapture(handoff.OriginalHandle, identity, cancellationToken)
                ?? throw new IOException("The fixture did not return the original live candidate launch.");

            image.Verify(cancellationToken);
            if (process.ReadLive(cancellationToken) is null)
            {
                throw new IOException("The fixture candidate exited during the launch handoff.");
            }

            return new(image, process,
                new(identity.ProcessId, identity.CreationFileTimeUtc, identity.ExecutablePath, expected.Sha256));
        }
        catch
        {
            process?.Dispose();
            image.Dispose();
            throw;
        }
    }

    public async ValueTask<SetupCandidateLaunch?> ReadLiveAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidenceLost)
            {
                return null;
            }

            string digest = image.Verify(cancellationToken);
            if (process.ReadLive(cancellationToken) is { } live)
            {
                return new(live.ProcessId, live.CreationFileTimeUtc, live.ExecutablePath, digest);
            }
            evidenceLost = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Evidence cannot be reacquired after an uncertain read.
            evidenceLost = true;
        }
        finally
        {
            gate.Release();
        }

        return null;
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            evidenceLost = true;
            process.Dispose();
            image.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }
}
