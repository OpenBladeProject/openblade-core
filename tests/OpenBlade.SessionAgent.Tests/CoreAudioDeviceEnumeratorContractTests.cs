namespace OpenBlade.SessionAgent.Tests;

public sealed class CoreAudioDeviceEnumeratorContractTests
{
    [Fact]
    public void CoreAudioConsumersUseOneCoclassFreeActivationPath()
    {
        string[] consumers =
        [
            "MicrophoneMuteStateProbeCommand.cs",
            "ServiceAudioMeterLightingLoop.cs",
            "SpecialKeyActionExecutor.cs",
            "WindowsCoreAudioMuteStateSource.cs",
        ];

        foreach (string consumer in consumers)
        {
            string source = File.ReadAllText(RepositoryFile(
                "src",
                "OpenBlade.SessionAgent",
                consumer));
            Assert.Contains(
                "CoreAudioDeviceEnumerator.Create()",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "class MmDeviceEnumerator",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "BCDE0395-E52F-467C-8E3D-C4579291692E",
                source,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "FinalReleaseComObject",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MuteCallbacksUsePayloadsAndDeferEndpointRebinding()
    {
        string source = File.ReadAllText(RepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "WindowsCoreAudioMuteStateSource.cs"));

        Assert.Contains(
            "Marshal.PtrToStructure<AudioVolumeNotificationData>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "rebindRequests.Writer.TryWrite(0)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void PublishUnknown() => publish(null);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("lock (", source, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] path)
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName
                ?? throw new DirectoryNotFoundException(
                    "The repository root could not be located.");
        }

        return Path.Combine([directory, .. path]);
    }
}
