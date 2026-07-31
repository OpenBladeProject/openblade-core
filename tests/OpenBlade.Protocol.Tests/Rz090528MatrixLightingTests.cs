using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528MatrixLightingTests
{
    private static readonly int[] ExpectedHoleSlots =
        [0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93];

    [Fact]
    public void UniformFrame_UsesOnlyTheIndependentlyCapturedPid02C6HoleMask()
    {
        var color = new RgbColor(0x47, 0xE1, 0x0C);

        Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(color);

        Assert.Equal(ExpectedHoleSlots, Rz090528MatrixFrame.CapturedHoleSlots);
        for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
        {
            bool hole = ExpectedHoleSlots.Contains(slot);
            Assert.Equal(hole, Rz090528MatrixFrame.IsHole(slot));
            Assert.Equal(hole ? default : color, frame[slot]);
        }
    }

    [Fact]
    public void RowCodec_EmitsCapturedFfRowZeroToSixteenHeaderAndRgbOrder()
    {
        var color = new RgbColor(0x47, 0xE1, 0x0C);
        Rz090528MatrixFrame frame = Rz090528MatrixFrame.Uniform(color);

        for (int row = 0; row < Rz090528MatrixFrame.RowCount; row++)
        {
            byte[] payload = Rz090528MatrixRowProtocol.EncodeRow(row, frame);

            Assert.Equal(Rz090528MatrixRowProtocol.RowPayloadLength, payload.Length);
            Assert.Equal(0xFF, payload[0]);
            Assert.Equal(row, payload[1]);
            Assert.Equal(0x00, payload[2]);
            Assert.Equal(0x10, payload[3]);
            for (int column = 0; column < Rz090528MatrixFrame.ColumnCount; column++)
            {
                int slot = (row * Rz090528MatrixFrame.ColumnCount) + column;
                int offset = 4 + (column * 3);
                RgbColor expected = Rz090528MatrixFrame.IsHole(slot)
                    ? default
                    : color;
                Assert.Equal(expected.Red, payload[offset]);
                Assert.Equal(expected.Green, payload[offset + 1]);
                Assert.Equal(expected.Blue, payload[offset + 2]);
            }
        }
    }

    [Fact]
    public void Spectrum_TraversesRgbWheelAcrossExactMeasuredCycle()
    {
        var effect = new Rz090528SpectrumEffect();

        Assert.True(effect.RequiresContinuousRendering);
        Assert.Equal(
            new RgbColor(0xFF, 0x00, 0x00),
            effect.RenderFrame(TimeSpan.Zero)[1]);
        Assert.Equal(
            new RgbColor(0x80, 0xFF, 0x00),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration / 4)[1]);
        Assert.Equal(
            new RgbColor(0x00, 0xFF, 0xFF),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration / 2)[1]);
        Assert.Equal(
            new RgbColor(0x80, 0x00, 0xFF),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration * 3 / 4)[1]);
        Assert.Equal(
            new RgbColor(0xFF, 0x00, 0x00),
            effect.RenderFrame(Rz090528SpectrumEffect.CycleDuration)[1]);
    }

    [Fact]
    public void Static_IsAOneShotFrameWithoutAnUncapturedKeepalive()
    {
        var effect = new Rz090528StaticEffect(new RgbColor(0x47, 0xE1, 0x0C));
        Rz090528MatrixFrame initial = effect.RenderFrame(TimeSpan.Zero);
        Rz090528MatrixFrame later = effect.RenderFrame(TimeSpan.FromHours(1));

        Assert.False(effect.RequiresContinuousRendering);
        Assert.Equal(Timeout.InfiniteTimeSpan, effect.NextFrameDelay);
        for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
        {
            Assert.Equal(initial[slot], later[slot]);
        }
    }

    [Fact]
    public async Task CommandSessionGate_SerializesIndependentPid02C6Sessions()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var first = new BlockingSession();
        var second = new BlockingSession();
        var firstSession = new Rz090528CommandSessionGate(first, gate);
        var secondSession = new Rz090528CommandSessionGate(second, gate);
        RazerCommandSpec command = new(0x03, 0x83, [0x01, 0x05, 0x00], 3);

        Task<HidSessionResult<ReadOnlyMemory<byte>>> firstExchange =
            firstSession.ExecuteAsync(
                command,
                static () => TransactionIdAssignment.Valid(1),
                TimeSpan.FromSeconds(1),
                default);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Task<HidSessionResult<ReadOnlyMemory<byte>>> secondExchange =
            secondSession.ExecuteAsync(
                command,
                static () => TransactionIdAssignment.Valid(1),
                TimeSpan.FromSeconds(1),
                default);
        await Task.Delay(50);
        Assert.False(second.Entered.Task.IsCompleted);

        first.Release.TrySetResult();
        Assert.Null((await firstExchange).Failure);
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        second.Release.TrySetResult();
        Assert.Null((await secondExchange).Failure);
    }

    private sealed class BlockingSession : IRazerCommandSession
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = command;
            _ = assignTransactionId();
            _ = exchangeTimeout;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(new byte[] { 0x01, 0x05, 0x00 }, null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
