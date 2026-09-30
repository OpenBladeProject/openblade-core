using System.Runtime.InteropServices;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsSavedCpuWmiOutputTests
{
    [Fact]
    public void MissingReturnRequiresExplicitPinnedCompatibility()
    {
        var missing = WindowsSavedCpuWmiOutput.Read(["version"], _ => throw new InvalidOperationException(), "ReturnValue");
        Assert.Equal(CpuSavedWmiPropertyState.Absent, missing.State);
        Assert.False(WindowsSavedCpuWmiOutput.AcceptReturnValue(missing, false));
        Assert.True(WindowsSavedCpuWmiOutput.AcceptReturnValue(missing, true));
        Assert.False(WindowsSavedCpuWmiOutput.AcceptVersion(missing));
        Assert.False(WindowsSavedCpuWmiOutput.AcceptCommandResult(missing));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData("true", false)]
    public void PresentReturnValueMustBeBooleanTrue(object? value, bool expected)
    {
        var property = WindowsSavedCpuWmiOutput.Read(["RETURNVALUE"], _ => value, "ReturnValue");
        Assert.Equal(expected, WindowsSavedCpuWmiOutput.AcceptReturnValue(property, true));
        Assert.Equal(expected, WindowsSavedCpuWmiOutput.AcceptReturnValue(property, false));
    }

    [Fact]
    public void FailedEnumerationNeverEstablishesAbsenceOrReadsFoundValue()
    {
        int reads = 0;
        foreach (string name in new[] { "ReturnValue", "unrelated" })
        {
            var property = WindowsSavedCpuWmiOutput.Read(FailingNames(name), _ => { reads++; return true; }, "ReturnValue");
            Assert.Equal(CpuSavedWmiPropertyState.Error, property.State);
            Assert.False(WindowsSavedCpuWmiOutput.AcceptReturnValue(property, true));
        }
        Assert.Equal(0, reads);
    }

    [Fact]
    public void FoundPropertyReadFailureIsNeverMissingCompatibility()
    {
        foreach (Exception failure in new Exception[] { new COMException("missing", unchecked((int)0x80041002)),
            new InvalidOperationException(), new ObjectDisposedException("output"), new OperationCanceledException() })
        {
            var property = WindowsSavedCpuWmiOutput.Read(["ReturnValue"], _ => throw failure, "ReturnValue");
            Assert.Equal(CpuSavedWmiPropertyState.Error, property.State);
            Assert.False(WindowsSavedCpuWmiOutput.AcceptReturnValue(property, true));
            Assert.False(WindowsSavedCpuWmiOutput.AcceptVersion(property));
            Assert.False(WindowsSavedCpuWmiOutput.AcceptCommandResult(property));
        }
    }

    [Fact]
    public void MalformedDuplicateOrUnboundedEnumerationFailsClosed()
    {
        foreach (IEnumerable<string> names in new IEnumerable<string>[] { ["ReturnValue", "returnvalue"],
            [""], [new string('x', 129)], Enumerable.Range(0, 65).Select(index => "p" + index) })
        {
            Assert.Equal(CpuSavedWmiPropertyState.Error,
                WindowsSavedCpuWmiOutput.Read(names, _ => true, "ReturnValue").State);
        }
    }

    [Fact]
    public void VersionRequiresExactUInt32Seven()
    {
        foreach (object? value in new object?[] { null, 7, 7L, (ushort)7, "7", true, 6u, 8u })
        { Assert.False(WindowsSavedCpuWmiOutput.AcceptVersion(new(CpuSavedWmiPropertyState.Present, value))); }
        Assert.True(WindowsSavedCpuWmiOutput.AcceptVersion(new(CpuSavedWmiPropertyState.Present, 7u)));
    }

    [Fact]
    public void CommandResultRequiresBoundedByteArrayWithSuccessFirstByte()
    {
        foreach (object? value in new object?[] { null, new byte[0], new byte[4097], new byte[] { 1 },
            new int[] { 0 }, "0", 0 })
        { Assert.False(WindowsSavedCpuWmiOutput.AcceptCommandResult(new(CpuSavedWmiPropertyState.Present, value))); }
        Assert.True(WindowsSavedCpuWmiOutput.AcceptCommandResult(new(CpuSavedWmiPropertyState.Present, new byte[] { 0 })));
        Assert.True(WindowsSavedCpuWmiOutput.AcceptCommandResult(new(CpuSavedWmiPropertyState.Present, new byte[4096])));
    }

    private static IEnumerable<string> FailingNames(string name)
    {
        yield return name;
        throw new COMException("unrelated property failure", unchecked((int)0x80041002));
    }
}
