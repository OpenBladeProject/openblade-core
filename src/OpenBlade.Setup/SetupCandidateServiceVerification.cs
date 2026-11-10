namespace OpenBlade.Setup;

// The handoff supplies the observed incoming generation and the expected digest
// from its independently selected payload before readiness begins.
internal sealed record SetupCandidateLaunch(
    uint ProcessId, long CreationFileTimeUtc, string ExecutablePath, string ExecutableSha256);

internal sealed record SetupCandidateServiceObservation(
    uint ProcessId, bool Running, uint ServiceType, uint StartType, string Account, string CommandLine);

internal interface ISetupRetainedCandidateLaunch
{
    // Read the original process handle and the build pin held continuously from
    // before launch. Return null for an exited process or lost evidence. A new
    // handle opened by PID or a fresh hash of the pathname cannot replace either.
    // The observer must keep its retained evidence available for the read and
    // honor cancellation if it has to wait for it.
    ValueTask<SetupCandidateLaunch?> ReadLiveAsync(CancellationToken cancellationToken);
}

internal sealed class SetupCandidateServiceVerification
{
    private const uint Win32OwnProcess = 0x10;
    private const uint AutoStart = 2;
    private readonly SetupCandidateLaunch expected;
    private readonly ISetupRetainedCandidateLaunch retained;
    private readonly Func<CancellationToken, Task<SetupCandidateServiceObservation>> readService;

    internal SetupCandidateServiceVerification(
        SetupCandidateLaunch expected,
        ISetupRetainedCandidateLaunch retained,
        Func<CancellationToken, Task<SetupCandidateServiceObservation>> readService)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.ProcessId == 0 || expected.CreationFileTimeUtc <= 0
            || !Path.IsPathFullyQualified(expected.ExecutablePath)
            || expected.ExecutablePath.Contains('"')
            || !string.Equals(expected.ExecutablePath, Path.GetFullPath(expected.ExecutablePath), StringComparison.Ordinal)
            || expected.ExecutableSha256 is not { Length: 64 }
            || !expected.ExecutableSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An exact incoming candidate launch identity is required.", nameof(expected));
        }

        this.expected = expected;
        this.retained = retained ?? throw new ArgumentNullException(nameof(retained));
        this.readService = readService ?? throw new ArgumentNullException(nameof(readService));
    }

    internal void VerifyPeer(Stream connection)
    {
        if (SetupServiceHealthTiming.ReadPeerProcessId(connection) != expected.ProcessId)
        {
            throw new IOException("The readiness pipe server does not match the retained candidate service.");
        }
    }

    internal async Task VerifyLaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetupCandidateServiceObservation service = await readService(cancellationToken).WaitAsync(cancellationToken);
        if (!service.Running || service.ProcessId != expected.ProcessId
            || service.ServiceType != Win32OwnProcess || service.StartType != AutoStart
            || !string.Equals(service.Account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(service.CommandLine, $"\"{expected.ExecutablePath}\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The fixed SCM service does not match the incoming candidate launch.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetupCandidateLaunch? observed = await retained.ReadLiveAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (observed is null || observed.ProcessId != expected.ProcessId
            || observed.CreationFileTimeUtc != expected.CreationFileTimeUtc
            || !string.Equals(observed.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(observed.ExecutableSha256, expected.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The retained candidate process generation or executable build changed.");
        }
    }
}
