using System.Text;
using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed class IpcJsonValidationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"name\":1,\"nested\":{\"name\":2}}")]
    [InlineData("[{\"name\":1},{\"name\":2}]")]
    [InlineData("{\"values\":[true,false,null,{},[1,2]]} \r\n")]
    public void CompleteValuesWithDistinctMembersAreAccepted(string json)
    {
        IpcJsonValidation.ValidateNoDuplicateMembers(Encoding.UTF8.GetBytes(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("[")]
    [InlineData("{\"name\":")]
    [InlineData("{\"name\":1")]
    [InlineData("[1")]
    [InlineData("{}{}")]
    [InlineData("{} null")]
    [InlineData("{\"name\":1,}")]
    [InlineData("{/*comment*/}")]
    [InlineData("{\"name\":1,\"name\":2}")]
    [InlineData("{\"name\":1,\"NAME\":2}")]
    [InlineData("{\"name\":1,\"n\\u0061me\":2}")]
    [InlineData("{\"nested\":{\"name\":1,\"NAME\":2}}")]
    [InlineData("[{\"nested\":[{\"name\":1,\"NAME\":2}]}]")]
    public void InvalidOrDuplicateMembersAreRejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() =>
            IpcJsonValidation.ValidateNoDuplicateMembers(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void DepthLimitMatchesTheExistingDocumentParser()
    {
        string valid = new string('[', 64) + new string(']', 64);
        IpcJsonValidation.ValidateNoDuplicateMembers(Encoding.UTF8.GetBytes(valid));
        string invalid = '[' + valid + ']';
        Assert.ThrowsAny<JsonException>(() =>
            IpcJsonValidation.ValidateNoDuplicateMembers(Encoding.UTF8.GetBytes(invalid)));
    }
}
