using System.Text;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioProvisioningJournalTests
{
    [Theory]
    [InlineData("{\"kind\":\"baseline\"}\n", false)]
    [InlineData("{\"kind\":\"intent\"}\n", true)]
    [InlineData("{\"kind\":\"intent\"}\n{\"kind\":\"native-result\"}\n", true)]
    [InlineData("{\"kind\":\"intent\",\"step\":0}\n{\"kind\":\"complete\"}\n", false)]
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
    [InlineData(2)]
    public void RestartObservationBeforeNextIntentRetainsUnsubmittedContinuation(int step)
    {
        string json = $$$"""
            {"kind":"baseline","packageDestination":"C:\\ProgramData\\OpenBlade.ThxAudioProvisioning\\0123456789abcdef0123456789abcdef","baseline":{"Profile":"RZ09-0528/rznb11"}}
            {"kind":"intent","step":{{{step}}}}
            {"kind":"native-result","bootIdentity":"boot-a","result":{"ExitCode":3010,"Output":""}}
            {"kind":"observed","step":{{{step}}}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ThxAudioProvisioningRecovery recovery = Assert.IsType<ThxAudioProvisioningRecovery>(
            ThxAudioProvisioningJournal.ReadRecovery(stream));
        Assert.Equal((ThxAudioProvisioningStep)step, recovery.RestartStep);
        Assert.Equal(recovery.RestartStep, recovery.LastCompletedStep);
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
            {"kind":"observed","step":0}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Null(ThxAudioProvisioningJournal.ReadRecovery(stream));
    }
    [Theory]
    [InlineData("observed", "999")]
    [InlineData("observed", "-1")]
    [InlineData("intent", "999")]
    [InlineData("intent", "null")]
    public void InvalidCheckpointCannotResetTheSequenceOrPermitRestartContinuation(string kind, string step)
    {
        string records = $$$"""
            {"kind":"baseline","formatVersion":2,"packageDestination":"protected-package","baseline":{"Profile":"model/recipe-v2","BaseAudioTarget":"root-a"}}
            {"kind":"intent","step":3}
            {"kind":"native-result","bootIdentity":"boot-a","result":{"ExitCode":3010}}
            {"kind":"{{{kind}}}","step":{{{step}}}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(records));
        Assert.True(ThxAudioProvisioningJournal.HasUnfinishedTransaction(stream));
        stream.Position = 0;
        Assert.Null(ThxAudioProvisioningJournal.ReadRecovery(stream));
    }
    [Fact]
    public void NewBaseCheckpointRequiresVersionedJournalWithoutRenumberingOldSteps()
    {
        Assert.Equal(0, (int)ThxAudioProvisioningStep.InstallDriverStack);
        Assert.Equal(1, (int)ThxAudioProvisioningStep.InstallProperties);
        Assert.Equal(2, (int)ThxAudioProvisioningStep.InstallSurroundConfiguration);
        Assert.Equal(3, (int)ThxAudioProvisioningStep.InstallBaseAudio);
        string records = """
            {"kind":"baseline","formatVersion":2,"packageDestination":"protected-package","baseline":{"Profile":"model/recipe-v2","BaseAudioTarget":"root-a"}}
            {"kind":"intent","step":3}
            {"kind":"native-result","bootIdentity":"boot-a","result":{"ExitCode":3010}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(records));
        ThxAudioProvisioningRecovery recovered = Assert.IsType<ThxAudioProvisioningRecovery>(
            ThxAudioProvisioningJournal.ReadRecovery(stream));
        Assert.Equal(ThxAudioProvisioningStep.InstallBaseAudio, recovered.RestartStep);
        Assert.Equal("root-a", recovered.BaseAudioTarget);
        using var legacy = new MemoryStream(Encoding.UTF8.GetBytes(
            records.Replace("\"formatVersion\":2,", string.Empty, StringComparison.Ordinal)));
        Assert.Null(ThxAudioProvisioningJournal.ReadRecovery(legacy));
    }

    [Fact]
    public void VersionedSequenceCannotSkipBaseCheckpoint()
    {
        string records = """
            {"kind":"baseline","formatVersion":2,"packageDestination":"protected-package","baseline":{"Profile":"model/recipe-v2","BaseAudioTarget":"root-a"}}
            {"kind":"intent","step":0}
            {"kind":"native-result","bootIdentity":"boot-a","result":{"ExitCode":3010}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(records));
        Assert.Null(ThxAudioProvisioningJournal.ReadRecovery(stream));
        string withBase = records.Replace("{\"kind\":\"intent\",\"step\":0}",
            "{\"kind\":\"observed\",\"step\":3}\n{\"kind\":\"intent\",\"step\":0}", StringComparison.Ordinal);
        using var ordered = new MemoryStream(Encoding.UTF8.GetBytes(withBase));
        ThxAudioProvisioningRecovery recovered = Assert.IsType<ThxAudioProvisioningRecovery>(
            ThxAudioProvisioningJournal.ReadRecovery(ordered));
        Assert.Equal(ThxAudioProvisioningStep.InstallDriverStack, recovered.RestartStep);
    }

}
