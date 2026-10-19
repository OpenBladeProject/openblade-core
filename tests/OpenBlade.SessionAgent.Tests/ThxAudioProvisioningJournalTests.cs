using System.Text;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioProvisioningJournalTests
{
    [Theory]
    [InlineData("{\"kind\":\"baseline\"}\n", false)]
    [InlineData("{\"kind\":\"intent\"}\n", true)]
    [InlineData("{\"kind\":\"intent\"}\n{\"kind\":\"native-result\"}\n", true)]
    [InlineData("{\"kind\":\"intent\"}\n{\"kind\":\"complete\"}\n", false)]
    public void OnlyConfirmedCompletionClearsIntent(string json, bool pending)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Equal(pending, ThxAudioProvisioningJournal.HasUnfinishedTransaction(stream));
    }

    [Fact]
    public void TruncatedJournalCannotBeReadAsCompleted()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"kind\":\"intent\"}\n{\"kind\":\"comp"));
        Assert.True(ThxAudioProvisioningJournal.HasUnfinishedTransaction(stream));
        stream.Position = 0;
        Assert.Null(ThxAudioProvisioningJournal.ReadRecovery(stream));
    }

    [Fact]
    public void OnlyExactKnownRestartSequenceIsResumable()
    {
        string json = """
            {"kind":"baseline","packageDestination":"C:\\ProgramData\\OpenBlade.ThxAudioProvisioning\\0123456789abcdef0123456789abcdef","baseline":{"Profile":"RZ09-0528/rznb11"}}
            {"kind":"intent","step":0}
            {"kind":"native-result","bootIdentity":"boot-a","result":{"ExitCode":3010,"Output":""}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ThxAudioProvisioningRecovery recovery = Assert.IsType<ThxAudioProvisioningRecovery>(
            ThxAudioProvisioningJournal.ReadRecovery(stream));
        Assert.Equal(ThxAudioProvisioningStep.InstallDriverStack, recovery.RestartStep);
        Assert.Equal("boot-a", recovery.ResultBootIdentity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1602)]
    public void AmbiguousOrOrdinaryExitIsNeverResumable(int exitCode)
    {
        string json = $$$"""
            {"kind":"baseline","packageDestination":"C:\\ProgramData\\OpenBlade.ThxAudioProvisioning\\0123456789abcdef0123456789abcdef","baseline":{"Profile":"RZ09-0528/rznb11"}}
            {"kind":"intent","step":0}
            {"kind":"native-result","bootIdentity":"boot-a","result":{"ExitCode":{{{exitCode}}},"Output":""}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Null(ThxAudioProvisioningJournal.ReadRecovery(stream));
    }
}
