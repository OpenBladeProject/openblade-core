using System.Text;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class BrokerProtocolTests
{
    private static readonly Guid RequestId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");

    [Fact]
    public void RequestCodec_RoundTripsOnlyFixedInboxContract()
    {
        DeploymentBrokerRequest request = new(
            DeploymentBrokerProtocol.ProtocolVersion,
            RequestId,
            DeploymentBrokerAction.ApplyFixedInbox);

        byte[] bytes = DeploymentBrokerProtocol.SerializeRequest(request);

        Assert.Equal(request, DeploymentBrokerProtocol.ParseRequest(bytes));
        Assert.Equal(
            "{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\","
                + "\"action\":\"ApplyFixedInbox\"}",
            Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\",\"path\":\"C:/evil.exe\"}")]
    [InlineData("{\"protocolVersion\":1,\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\"}")]
    [InlineData("{ \"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\"}")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"Other\"}")]
    public void RequestCodec_RejectsUnknownDuplicateNoncanonicalOrOtherAction(string json)
    {
        Assert.Throws<InvalidDataException>(
            () => DeploymentBrokerProtocol.ParseRequest(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ResponseCodec_AcceptsOnlyCanonicalBoundedResponse()
    {
        DeploymentBrokerResponse response = Success();
        byte[] bytes = DeploymentBrokerProtocol.SerializeResponse(response);

        Assert.Equal(response, DeploymentBrokerProtocol.ParseResponse(bytes));
        Assert.True(DeploymentBrokerProtocol.IsExactSuccess(response, RequestId));
        Assert.False(DeploymentBrokerProtocol.IsExactSuccess(
            response with { Code = "other-success" },
            RequestId));
        Assert.False(DeploymentBrokerProtocol.IsExactSuccess(
            response with { RequestId = Guid.NewGuid() },
            RequestId));
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"status\":\"Succeeded\",\"code\":\"install-succeeded\",\"command\":\"evil\"}")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"status\":\"Succeeded\",\"status\":\"Succeeded\",\"code\":\"install-succeeded\"}")]
    [InlineData("{ \"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"status\":\"Succeeded\",\"code\":\"install-succeeded\"}")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"status\":\"Unknown\",\"code\":\"install-succeeded\"}")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"status\":\"Succeeded\",\"code\":\"Deployment-Succeeded\"}")]
    public void ResponseCodec_RejectsUnknownDuplicateNoncanonicalOrMalformed(string json)
    {
        Assert.Throws<InvalidDataException>(
            () => DeploymentBrokerProtocol.ParseResponse(Encoding.UTF8.GetBytes(json)));
    }

    internal static DeploymentBrokerResponse Success() => new(
        DeploymentBrokerProtocol.ProtocolVersion,
        RequestId,
        DeploymentBrokerResponseStatus.Succeeded,
        DeploymentBrokerProtocol.SuccessCode);
}
