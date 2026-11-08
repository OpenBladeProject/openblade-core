using System.Runtime.InteropServices;
using OpenBlade.Core.Native;

namespace OpenBlade.Core.Native.Tests;

public sealed unsafe class NativeAbiTests
{
    [Fact]
    public void ManagedRecordsMatchFrozenNaturalLayout()
    {
        Assert.Equal(8, sizeof(NativeReportRequest));
        Assert.Equal(12, sizeof(NativeValidationOptions));
        Assert.Equal(16, sizeof(NativeErrorRecord));
        Assert.Equal(0, Offset<NativeReportRequest>(nameof(NativeReportRequest.StructSize)));
        Assert.Equal(4, Offset<NativeReportRequest>(nameof(NativeReportRequest.Transaction)));
        Assert.Equal(5, Offset<NativeReportRequest>(nameof(NativeReportRequest.CommandClass)));
        Assert.Equal(6, Offset<NativeReportRequest>(nameof(NativeReportRequest.CommandId)));
        Assert.Equal(7, Offset<NativeReportRequest>(nameof(NativeReportRequest.Reserved)));
        Assert.Equal(4, Offset<NativeValidationOptions>(nameof(NativeValidationOptions.ExpectedLength)));
        Assert.Equal(8, Offset<NativeValidationOptions>(nameof(NativeValidationOptions.HasExpected)));
        Assert.Equal(9, Offset<NativeValidationOptions>(nameof(NativeValidationOptions.Reserved0)));
        Assert.Equal(10, Offset<NativeValidationOptions>(nameof(NativeValidationOptions.Reserved1)));
        Assert.Equal(11, Offset<NativeValidationOptions>(nameof(NativeValidationOptions.Reserved2)));
        Assert.Equal(4, Offset<NativeErrorRecord>(nameof(NativeErrorRecord.Category)));
        Assert.Equal(8, Offset<NativeErrorRecord>(nameof(NativeErrorRecord.Detail)));
        Assert.Equal(12, Offset<NativeErrorRecord>(nameof(NativeErrorRecord.RequiredLength)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(89)]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(256)]
    public void CapacityContractPreservesCanariesAndReportsRequiredSize(int capacity)
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        NativeReportRequest request = NativeReportRequest.Create(28, 13, 2);
        NativeErrorRecord error = NativeErrorRecord.Create();
        byte[] destination = Enumerable.Repeat((byte)0xA5, capacity + 2).ToArray();
        uint written = 0xFFFF;
        fixed (byte* bytes = destination)
        {
            uint status = codec.RawEncode(NativeReportCodec.AbiVersion, &request, null, 0,
                capacity == 0 ? null : bytes + 1, (uint)capacity, &written, &error);
            Assert.Equal(capacity < 90 ? 3u : 0u, status);
            Assert.Equal(status, error.Category);
            Assert.Equal(90u, written);
            Assert.Equal(capacity < 90 ? 90u : 0u, error.RequiredLength);
        }
        Assert.Equal(0xA5, destination[0]);
        Assert.Equal(0xA5, destination[^1]);
        if (capacity < 90)
        {
            Assert.All(destination, value => Assert.Equal(0xA5, value));
        }
        else
        {
            Assert.Equal(codec.Encode(28, 13, 2, []).Output, destination.AsSpan(1, 90).ToArray());
            Assert.All(destination.Skip(91), value => Assert.Equal(0xA5, value));
        }
    }

    [Fact]
    public void VersionsRecordsFlagsAndNullConventionsRejectWithoutResultWrites()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        for (int fault = 0; fault < 9; fault++)
        {
            NativeReportRequest request = NativeReportRequest.Create(1, 2, 3);
            NativeErrorRecord error = NativeErrorRecord.Create();
            byte[] destination = Enumerable.Repeat((byte)0xA5, 92).ToArray();
            uint written = 0xFFFF;
            uint version = NativeReportCodec.AbiVersion;
            if (fault == 0) { version++; }
            if (fault == 1) { request.StructSize--; }
            if (fault == 2) { request.Reserved = 1; }
            if (fault == 7) { error.StructSize--; }
            fixed (byte* output = destination)
            {
                uint status = codec.RawEncode(version, fault == 3 ? null : &request,
                    null, fault == 4 ? 1u : fault == 8 ? 81u : 0u,
                    fault == 5 ? null : output + 1, 90,
                    fault == 6 ? null : &written, &error);
                Assert.Equal(fault == 0 ? 1u : 2u, status);
                if (fault != 7) { Assert.Equal(status, error.Category); }
                else { Assert.Equal(15u, error.StructSize); Assert.Equal(0u, error.Category); }
                if (fault != 6) { Assert.Equal(0u, written); }
            }
            Assert.All(destination, value => Assert.Equal(0xA5, value));
        }
    }

    [Fact]
    public void VersionIsRejectedBeforeReadingOperationRecords()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        NativeErrorRecord error = NativeErrorRecord.Create();
        uint written = 42;
        Assert.Equal(1u, codec.RawEncode(0, null, null, 0, null, 0, &written, &error));
        Assert.Equal(0u, written);
        Assert.Equal(1u, codec.RawValidate(0, null, null, 0, null, 0, null, null, 0, &written, &error));
        Assert.Equal(0u, written);
    }

    [Fact]
    public void OptionalErrorRecordAndEmptySlicesAreAccepted()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        NativeReportRequest request = NativeReportRequest.Create(0, 0, 0);
        uint written;
        byte* report = stackalloc byte[90];
        Assert.Equal(0u, codec.RawEncode(NativeReportCodec.AbiVersion, &request, null, 0, report, 90, &written, null));
        Assert.Equal(90u, written);
        report[0] = 2;
        NativeValidationOptions options = NativeValidationOptions.Create(null);
        Assert.Equal(0u, codec.RawValidate(NativeReportCodec.AbiVersion, &request, null, 0,
            report, 90, &options, null, 0, &written, null));
        Assert.Equal(0u, written);
    }

    [Fact]
    public void ResponseQueriesAndSignedLengthsPreserveFailureIntegrity()
    {
        using NativeReportCodec codec = NativeParityTests.Load();
        NativeReportRequest request = NativeReportRequest.Create(0, 0, 0);
        byte[] response = codec.Encode(0, 0, 0, [1, 2, 3]).Output!;
        response[0] = 2;
        foreach (int? expected in new int?[] { null, 3, -1, int.MinValue, int.MaxValue })
        {
            NativeValidationOptions options = NativeValidationOptions.Create(expected);
            NativeErrorRecord error = NativeErrorRecord.Create();
            uint written = 42;
            fixed (byte* input = response)
            {
                uint status = codec.RawValidate(NativeReportCodec.AbiVersion, &request, null, 0,
                    input, 90, &options, null, 0, &written, &error);
                Assert.Equal(expected is null or 3 ? 3u : 4u, status);
                Assert.Equal(expected is null or 3 ? 3u : 0u, written);
                Assert.Equal(expected is null or 3 ? 0u : 7u, error.Detail);
            }
        }
        for (int fault = 0; fault < 6; fault++)
        {
            NativeValidationOptions options = NativeValidationOptions.Create(null);
            NativeErrorRecord error = NativeErrorRecord.Create();
            if (fault == 0) { options.StructSize--; }
            if (fault == 1) { options.HasExpected = 2; }
            if (fault == 2) { options.Reserved2 = 1; }
            uint written = 42;
            byte[] output = Enumerable.Repeat((byte)0xA5, 80).ToArray();
            fixed (byte* input = response)
            fixed (byte* destination = output)
            {
                uint status = codec.RawValidate(NativeReportCodec.AbiVersion, &request, null, 0,
                    fault == 4 ? null : input, fault == 5 ? 89u : 90u,
                    fault == 3 ? null : &options, destination, 80, &written, &error);
                Assert.Equal(fault == 5 ? 4u : 2u, status);
                Assert.Equal(0u, written);
                Assert.Equal(status, error.Category);
            }
            Assert.All(output, value => Assert.Equal(0xA5, value));
        }
    }

    private static int Offset<T>(string field) => Marshal.OffsetOf<T>(field).ToInt32();
}
