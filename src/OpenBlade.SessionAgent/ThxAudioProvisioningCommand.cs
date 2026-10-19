using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal enum ThxAudioProvisioningStep
{
    InstallDriverStack,
    InstallProperties,
    InstallSurroundConfiguration,
}

internal sealed record ThxAudioProvisioningSnapshot(
    bool Ready,
    bool Installed,
    ThxAudioProvisioningResult Refusal = ThxAudioProvisioningResult.Unavailable)
{
    public string Profile { get; init; } = string.Empty;
    public bool ExactHost { get; init; }
    public bool ExactDevices { get; init; }
    public bool AcPower { get; init; }
    public bool ControllersAbsent { get; init; }
    public bool StackHashes { get; init; }
    public bool CatalogMembership { get; init; }
    public bool Service { get; init; }
    public bool ProvisioningProcessesAbsent { get; init; }
    public string ActiveProperties { get; init; } = "not-inspected";
    public string SurroundConfiguration { get; init; } = "not-inspected";
}

internal sealed record ThxAudioProvisioningProcessResult(int ExitCode, string Output);

internal sealed record ThxAudioProvisioningRecovery(
    string PackageDestination,
    string Profile,
    string ResultBootIdentity,
    ThxAudioProvisioningStep RestartStep,
    ThxAudioProvisioningStep? LastCompletedStep);

internal interface IThxAudioProvisioningJournal : IDisposable
{
    bool RequiresRecovery { get; }
    ThxAudioProvisioningRecovery? Recovery { get; }
    string PackageDestination { get; }
    void Begin(ThxAudioProvisioningSnapshot baseline, string bootIdentity);
    void Intent(ThxAudioProvisioningStep step);
    void RecordResult(ThxAudioProvisioningProcessResult result, string bootIdentity);
    void Observe(ThxAudioProvisioningStep step, ThxAudioProvisioningSnapshot snapshot);
    void Complete();
}

internal interface IThxAudioProvisioningPlatform
{
    ThxAudioProvisioningSnapshot Capture(ThxAudioProvisioningStep? completedStep = null);
    string ReadBootIdentity();
    IThxAudioProvisioningJournal OpenJournal();
    Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken);
    Task<ThxAudioProvisioningProcessResult> ExecuteAsync(
        string root,
        ThxAudioProvisioningStep step,
        CancellationToken cancellationToken);
}

internal sealed class ThxAudioProvisioningCommand
{
    internal const string Argument = "--provision-thx-audio";
    private readonly IThxAudioProvisioningPlatform platform;
    private bool submissionMayHaveOccurred;

    internal ThxAudioProvisioningCommand()
        : this(new WindowsThxAudioProvisioningPlatform())
    {
    }

    internal ThxAudioProvisioningCommand(IThxAudioProvisioningPlatform platform)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    internal static bool HasCommandSwitch(IReadOnlyList<string> arguments) =>
        arguments.Any(value => value.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && string.Equals(arguments[0], Argument, StringComparison.OrdinalIgnoreCase);

    internal async Task<ThxAudioProvisioningResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IThxAudioProvisioningJournal journal = platform.OpenJournal();
            submissionMayHaveOccurred = journal.RequiresRecovery;
            if (journal.RequiresRecovery)
            {
                return await ResumeAfterRestartAsync(journal, cancellationToken).ConfigureAwait(false);
            }

            ThxAudioProvisioningSnapshot baseline = platform.Capture();
            if (baseline.Installed)
            {
                return ThxAudioProvisioningResult.AlreadyInstalled;
            }
            if (!baseline.Ready)
            {
                return baseline.Refusal;
            }

            string root = await platform.AcquireVerifiedAsync(journal.PackageDestination, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            baseline = platform.Capture();
            if (!baseline.Ready || baseline.Installed)
            {
                return baseline.Installed ? ThxAudioProvisioningResult.AlreadyInstalled : baseline.Refusal;
            }

            journal.Begin(baseline, platform.ReadBootIdentity());
            return await ExecuteRemainingAsync(journal, root, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return submissionMayHaveOccurred
                ? ThxAudioProvisioningResult.RecoveryRequired : ThxAudioProvisioningResult.Canceled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or CryptographicException
            or JsonException or TimeoutException or HttpRequestException or ManagementException or COMException)
        {
            return submissionMayHaveOccurred
                ? ThxAudioProvisioningResult.RecoveryRequired : ThxAudioProvisioningResult.Failed;
        }
    }

    private async Task<ThxAudioProvisioningResult> ResumeAfterRestartAsync(
        IThxAudioProvisioningJournal journal, CancellationToken cancellationToken)
    {
        ThxAudioProvisioningRecovery? recovery = journal.Recovery;
        if (recovery is null)
        {
            return ThxAudioProvisioningResult.RecoveryRequired;
        }
        string currentBoot = platform.ReadBootIdentity();
        if (string.Equals(currentBoot, recovery.ResultBootIdentity, StringComparison.Ordinal))
        {
            return ThxAudioProvisioningResult.RestartRequired;
        }
        ThxAudioProvisioningSnapshot observed = platform.Capture(recovery.RestartStep);
        if (!string.Equals(observed.Profile, recovery.Profile, StringComparison.Ordinal)
            || !observed.ProvisioningProcessesAbsent)
        {
            return ThxAudioProvisioningResult.RecoveryRequired;
        }
        if (!observed.Ready)
        {
            return observed.Refusal is ThxAudioProvisioningResult.AcPowerRequired
                or ThxAudioProvisioningResult.ControllerActive
                ? observed.Refusal
                : ThxAudioProvisioningResult.RecoveryRequired;
        }
        journal.Observe(recovery.RestartStep, observed);
        if (observed.Installed)
        {
            journal.Complete();
            return ThxAudioProvisioningResult.Installed;
        }
        return await ExecuteRemainingAsync(journal, recovery.PackageDestination,
            recovery.RestartStep, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ThxAudioProvisioningResult> ExecuteRemainingAsync(
        IThxAudioProvisioningJournal journal,
        string root,
        ThxAudioProvisioningStep? completedStep,
        CancellationToken cancellationToken)
    {
        foreach (ThxAudioProvisioningStep step in Enum.GetValues<ThxAudioProvisioningStep>()
            .Where(step => completedStep is null || step > completedStep))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThxAudioProvisioningSnapshot before = platform.Capture(completedStep);
            if (before.Installed)
            {
                journal.Complete();
                return ThxAudioProvisioningResult.Installed;
            }
            if (!before.Ready)
            {
                if (completedStep is null)
                {
                    journal.Complete();
                    return before.Refusal;
                }
                return ThxAudioProvisioningResult.RecoveryRequired;
            }
            journal.Intent(step);
            submissionMayHaveOccurred = true;
            ThxAudioProvisioningProcessResult result = await platform.ExecuteAsync(root, step, cancellationToken)
                .ConfigureAwait(false);
            journal.RecordResult(result, platform.ReadBootIdentity());
            if (result.ExitCode == (int)ThxAudioProvisioningResult.RestartRequired)
            {
                return ThxAudioProvisioningResult.RestartRequired;
            }
            if (result.ExitCode != 0)
            {
                return ThxAudioProvisioningResult.RecoveryRequired;
            }
            ThxAudioProvisioningSnapshot after = platform.Capture(step);
            journal.Observe(step, after);
            if (after.Installed)
            {
                journal.Complete();
                return ThxAudioProvisioningResult.Installed;
            }
            if (!after.Ready)
            {
                return ThxAudioProvisioningResult.RecoveryRequired;
            }
            completedStep = step;
        }

        ThxAudioProvisioningSnapshot final = platform.Capture(
            ThxAudioProvisioningStep.InstallSurroundConfiguration);
        if (!final.Installed)
        {
            return ThxAudioProvisioningResult.RecoveryRequired;
        }
        journal.Complete();
        return ThxAudioProvisioningResult.Installed;
    }
}
