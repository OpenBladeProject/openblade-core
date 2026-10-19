using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x029F, SourceBackedAckCommand.MaximumFans, "")]
    [InlineData(0x029F, SourceBackedAckCommand.MaximumFans, "00")]
    [InlineData(0x029F, SourceBackedAckCommand.FanMode, "")]
    [InlineData(0x029F, SourceBackedAckCommand.FanMode, "00010203")]
    [InlineData(0x029F, SourceBackedAckCommand.FanTarget, "")]
    [InlineData(0x029F, SourceBackedAckCommand.FanTarget, "000102")]
    [InlineData(0x029F, SourceBackedAckCommand.CustomLevel, "")]
    [InlineData(0x029F, SourceBackedAckCommand.CustomLevel, "000102")]
    [InlineData(0x02A0, SourceBackedAckCommand.MaximumFans, "")]
    [InlineData(0x02A0, SourceBackedAckCommand.MaximumFans, "00")]
    [InlineData(0x02A0, SourceBackedAckCommand.FanMode, "")]
    [InlineData(0x02A0, SourceBackedAckCommand.FanMode, "00010203")]
    [InlineData(0x02A0, SourceBackedAckCommand.FanTarget, "")]
    [InlineData(0x02A0, SourceBackedAckCommand.FanTarget, "000102")]
    [InlineData(0x02A0, SourceBackedAckCommand.CustomLevel, "")]
    [InlineData(0x02A0, SourceBackedAckCommand.CustomLevel, "000102")]
    public async Task SourceBackedProductsAck_TraversesRealSessionValidation(
        int pid,
        SourceBackedAckCommand commandKind,
        string payloadHex)
    {
        (ExperimentalBladePort port, RazerCommandSpec command) = await CaptureSourceBackedCommandAsync(pid, commandKind);
        await using (port)
        await using (WindowsRazerHidSession actor = CreateWireActor(
            Response(1, command.ClassId, command.CommandId, Convert.FromHexString(payloadHex))))
        {
            HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
                command,
                () => TransactionIdAssignment.Valid(1),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Assert.Null(result.Failure);
            Assert.Equal(Convert.FromHexString(payloadHex), result.Value.ToArray());
            Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        }
    }

    [Theory]
    [InlineData(0x029F, SourceBackedAckCommand.MaximumFans, "0000")]
    [InlineData(0x029F, SourceBackedAckCommand.FanMode, "0000000000")]
    [InlineData(0x029F, SourceBackedAckCommand.FanTarget, "00000000")]
    [InlineData(0x029F, SourceBackedAckCommand.CustomLevel, "00000000")]
    [InlineData(0x02A0, SourceBackedAckCommand.MaximumFans, "0000")]
    [InlineData(0x02A0, SourceBackedAckCommand.FanMode, "0000000000")]
    [InlineData(0x02A0, SourceBackedAckCommand.FanTarget, "00000000")]
    [InlineData(0x02A0, SourceBackedAckCommand.CustomLevel, "00000000")]
    public async Task SourceBackedProductsAck_RealSessionRejectsOversizedPayload(
        int pid,
        SourceBackedAckCommand commandKind,
        string payloadHex)
    {
        (ExperimentalBladePort port, RazerCommandSpec command) = await CaptureSourceBackedCommandAsync(pid, commandKind);
        await using (port)
        await using (WindowsRazerHidSession actor = CreateWireActor(
            Response(1, command.ClassId, command.CommandId, Convert.FromHexString(payloadHex))))
        {
            HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
                command,
                () => TransactionIdAssignment.Valid(1),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
            Assert.Equal(HidProtocolValidationFailure.PayloadValidator,
                result.Diagnostic?.ProtocolValidationFailure);
            Assert.NotEqual(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        }
    }

    [Theory]
    [InlineData(0x029F, SourceBackedAckCommand.MaximumFans, false, HidProtocolValidationFailure.Status)]
    [InlineData(0x029F, SourceBackedAckCommand.FanMode, true, HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(0x029F, SourceBackedAckCommand.FanTarget, true, HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(0x029F, SourceBackedAckCommand.CustomLevel, true, HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(0x02A0, SourceBackedAckCommand.MaximumFans, false, HidProtocolValidationFailure.Status)]
    [InlineData(0x02A0, SourceBackedAckCommand.FanMode, true, HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(0x02A0, SourceBackedAckCommand.FanTarget, true, HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(0x02A0, SourceBackedAckCommand.CustomLevel, true, HidProtocolValidationFailure.TransactionMismatch)]
    public async Task SourceBackedProductsAck_RealSessionRetainsStatusAndCorrelationChecks(
        int pid,
        SourceBackedAckCommand commandKind,
        bool mismatchedTransaction,
        HidProtocolValidationFailure expected)
    {
        (ExperimentalBladePort port, RazerCommandSpec command) = await CaptureSourceBackedCommandAsync(pid, commandKind);
        byte[] response = Response(
            mismatchedTransaction ? (byte)2 : (byte)1,
            command.ClassId,
            command.CommandId,
            [],
            mismatchedTransaction ? RazerCommandStatus.Successful : RazerCommandStatus.NotSupported);
        await using (port)
        await using (WindowsRazerHidSession actor = CreateWireActor(response))
        {
            HidSessionResult<ReadOnlyMemory<byte>> result = await actor.ExecuteAsync(
                command,
                () => TransactionIdAssignment.Valid(1),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
            Assert.Equal(expected, result.Diagnostic?.ProtocolValidationFailure);
            Assert.NotEqual(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        }
    }

    private static async Task<(ExperimentalBladePort Port, RazerCommandSpec Command)>
        CaptureSourceBackedCommandAsync(int pid, SourceBackedAckCommand commandKind)
    {
        var session = commandKind switch
        {
            SourceBackedAckCommand.MaximumFans => new Session
            {
                Mode = 4,
                MaximumAckPayloadOverride = [],
            },
            SourceBackedAckCommand.FanMode => new Session
            {
                FanModeAckPayloadOverride = [],
                FanTargetAckPayloadOverride = [],
            },
            SourceBackedAckCommand.CustomLevel => new Session
            {
                Mode = 4,
                Cpu = 1,
                Gpu = 2,
                CustomLevelAckPayloadOverride = [],
            },
            _ => new Session
            {
                Fan = 1,
                FanTargetAckPayloadOverride = [],
            },
        };
        ExperimentalBladePort port = Create(pid, session);
        if (commandKind == SourceBackedAckCommand.MaximumFans)
        {
            await port.SetCustomPerformanceAsync(
                CustomPerformanceSettings.Default with { MaximumFans = true },
                CancellationToken.None);
        }
        else if (commandKind == SourceBackedAckCommand.CustomLevel)
        {
            await port.SetCustomPerformanceAsync(
                new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High),
                CancellationToken.None);
        }
        else
        {
            await port.SetFanTargetsAsync(3000, 3000, CancellationToken.None);
        }

        byte commandId = commandKind switch
        {
            SourceBackedAckCommand.MaximumFans => 0x0F,
            SourceBackedAckCommand.FanMode => 0x02,
            SourceBackedAckCommand.CustomLevel => 0x07,
            _ => 0x01,
        };
        RazerCommandSpec command = session.Writes.First(item => item.CommandId == commandId);
        return (port, command);
    }

    private static WindowsRazerHidSession CreateWireActor(byte[] response)
    {
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        var handle = new OpenedHidHandle(file, bound, () => { }, () => { });
        var context = new ValidationRunContext();
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease lease));
        return new WindowsRazerHidSession(
            handle,
            new WireIo(response),
            new AllowGuard(),
            TimeProvider.System,
            lease,
            ControllerExclusivityPolicy.Strict);
    }

    private static byte[] Response(
        byte transactionId,
        byte commandClass,
        byte commandId,
        byte[] payload,
        RazerCommandStatus status = RazerCommandStatus.Successful)
    {
        byte[] report = RazerReport.Create(transactionId, commandClass, commandId, payload).Encode();
        report[0] = (byte)status;
        byte[] feature = new byte[91];
        report.CopyTo(feature, 1);
        return feature;
    }

    public enum SourceBackedAckCommand
    {
        MaximumFans,
        FanMode,
        FanTarget,
        CustomLevel,
    }

    private sealed class AllowGuard : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], null);
    }

    private sealed class WireIo(byte[] response) : IHidFeatureIo
    {
        private int calls;

        public Task<HidFeatureIoResult> ExecuteAsync(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            CancellationToken callerCancellation,
            CancellationToken deadlineCancellation)
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(new HidFeatureIoResult(
                    new NativeIoCompletion(true, 91, 0),
                    true,
                    HidFeatureTermination.Completed));
            }
            if (calls == 2)
            {
                response.CopyTo(buffer, 0);
                return Task.FromResult(new HidFeatureIoResult(
                    new NativeIoCompletion(true, 91, 0),
                    true,
                    HidFeatureTermination.Completed));
            }
            throw new InvalidOperationException("Unexpected native I/O.");
        }
    }
}
