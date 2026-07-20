using OpenBlade.InputAgent;
using OpenBlade.SessionAgent;

namespace OpenBlade.InputAgent.Tests;

public sealed class InputAgentLaunchArgumentsTests
{
    [Fact]
    public void Parse_AcceptsExactRandomizedParentContractInAnyOrder()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        string ready = $@"Local\OpenBlade.InputAgent.Ready.{nonce}";
        string shutdown = $@"Local\OpenBlade.InputAgent.Shutdown.{nonce}";

        InputAgentLaunchArguments parsed = InputAgentLaunchArguments.Parse(
        [
            UiAccessInputAgentArguments.ParentProcessId, "1234",
            UiAccessInputAgentArguments.ReadyEvent, ready,
            UiAccessInputAgentArguments.ParentStartedAtUtcTicks, "638900000000000000",
            UiAccessInputAgentArguments.ShutdownEvent, shutdown,
        ]);

        Assert.Equal(ready, parsed.ReadyEventName);
        Assert.Equal(shutdown, parsed.ShutdownEventName);
        Assert.Equal(1234, parsed.ParentProcessId);
        Assert.Equal(638900000000000000, parsed.ParentStartedAtUtcTicks);
    }

    public static IEnumerable<object[]> InvalidArguments
    {
        get
        {
            yield return [Array.Empty<string>()];
            yield return
            [
                new[] { "--ready-event", "Local\\OpenBlade.InputAgent.Ready.bad" },
            ];
            yield return
            [
                new[]
                {
                    "--ready-event", "Local\\OpenBlade.InputAgent.Ready.0123456789abcdef0123456789abcdef",
                    "--ready-event", "Local\\OpenBlade.InputAgent.Ready.0123456789abcdef0123456789abcdef",
                    "--parent-pid", "1",
                    "--parent-start-ticks", "1",
                },
            ];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Parse_RejectsIncompleteMalformedOrDuplicateContracts(string[] args)
    {
        Assert.Throws<ArgumentException>(() => InputAgentLaunchArguments.Parse(args));
    }

    [Fact]
    public void OrdinaryUnsignedTestProcess_DoesNotClaimUiAccess()
    {
        Assert.False(WindowsUiAccessState.IsEnabled());
    }
}
