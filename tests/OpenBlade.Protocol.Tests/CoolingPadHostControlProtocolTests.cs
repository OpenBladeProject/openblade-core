using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadHostControlProtocolTests
{
    [Fact]
    public void CurrentEvidence_RecordsPhysicalPairWithoutAdmittingProductionWrites()
    {
        CoolingPadFanControlEvidenceGates evidence =
            CoolingPadFanControlEvidenceGates.Current;

        Assert.True(evidence.ExactIdentityAndTransportReviewed);
        Assert.True(evidence.HostControl2200PairPhysicallyValidated);
        Assert.False(evidence.OpenBladeHandbackValidated);
        Assert.False(evidence.FullTargetRangeValidated);
        Assert.False(evidence.SmartCurveCadenceValidated);
        Assert.False(evidence.DisconnectReconnectValidated);
        Assert.False(evidence.LowPowerLifecycleValidated);
        Assert.False(evidence.CrashRecoveryValidated);
        Assert.False(evidence.HyperBoostInterlockValidated);
        Assert.False(evidence.PhysicalButtonOwnershipPolicyValidated);
        Assert.False(evidence.WriteAdmitted);
    }

    [Fact]
    public void DormantTypedCommands_EncodeEveryPlannedTargetAndRelease()
    {
        for (int rpm = CoolingPadFanTarget.MinimumRpm;
            rpm <= CoolingPadFanTarget.MaximumRpm;
            rpm += CoolingPadFanTarget.StepRpm)
        {
            var target = new CoolingPadFanTarget(rpm);
            CoolingPadHostControlPlan acquisition =
                CoolingPadHostControlPlan.CreateAcquisition(target);
            Assert.Equal(2, acquisition.Count);
            AssertCommand(acquisition.GetCommand(0), 0x10, [0x00, 0x06, 0x00]);
            AssertCommand(
                acquisition.GetCommand(1),
                0x01,
                [0x01, 0x05, checked((byte)(rpm / 50))]);
            AssertCommand(
                CoolingPadHostControlPlan.CreateUpdate(target),
                0x01,
                [0x01, 0x05, checked((byte)(rpm / 50))]);
        }

        AssertCommand(
            CoolingPadHostControlPlan.CreateRelease(),
            0x10,
            [0x00, 0x06, 0x00]);
    }

    [Fact]
    public void Validated2200Plan_ContainsOnlyExactOrderedPair()
    {
        CoolingPadHostControlPlan plan =
            CoolingPadHostControlPlan.CreateValidated2200();

        Assert.Equal(2, plan.Count);
        Assert.Equal(2_200, CoolingPadHostControlPlan.TargetRpm);
        Assert.Equal((byte)0x2C, CoolingPadHostControlPlan.TargetRaw);
        AssertCommand(plan.GetCommand(0), 0x10, [0x00, 0x06, 0x00]);
        AssertCommand(plan.GetCommand(1), 0x01, [0x01, 0x05, 0x2C]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Validated2200Plan_RejectsCommandsOutsideBoundedPair(int index)
    {
        CoolingPadHostControlPlan plan =
            CoolingPadHostControlPlan.CreateValidated2200();

        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetCommand(index));
    }

    [Fact]
    public void Validated2200Plan_RejectsMismatchedSemanticEchoes()
    {
        CoolingPadHostControlPlan plan =
            CoolingPadHostControlPlan.CreateValidated2200();

        Assert.False(plan.GetCommand(0).PayloadValidator!(
            new byte[] { 0x01, 0x06, 0x00 }));
        Assert.False(plan.GetCommand(1).PayloadValidator!(
            new byte[] { 0x01, 0x05, 0x2B }));
    }

    private static void AssertCommand(
        RazerCommandSpec command,
        byte expectedCommandId,
        byte[] expectedArguments)
    {
        Assert.Equal(0x0D, command.ClassId);
        Assert.Equal(expectedCommandId, command.CommandId);
        Assert.Equal(expectedArguments, command.Arguments.ToArray());
        Assert.Equal(expectedArguments.Length, command.ExpectedPayloadLength);
        Assert.True(command.PayloadValidator!(expectedArguments));
    }
}
