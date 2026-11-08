using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Guardian.TestHost;

internal sealed class FakeTrial
{
    private readonly string root;
    private readonly TimeProvider clock;
    private readonly So6120SessionBinding binding;
    private readonly MemoryPort port;

    public FakeTrial(string root, string gate = "fe-pending")
    {
        this.root = root;
        port = new MemoryPort(root, gate);
        clock = new SessionClock(gate == "zero-checkpoint" ? port.WaitAtGate : null);
        var identity = new So6120JournalIdentity(
            new(Guid.NewGuid(), "synthetic SO6120", "synthetic EC1.04", Guid.NewGuid()),
            PerformancePreset.Balanced, null, null, new('A', 64), new('B', 64), Guid.NewGuid(), new('D', 64),
            "synthetic-boot");
        var stamp = new So6120AdmissionStamp(identity.Owner.OperationId, 1, clock, clock.GetTimestamp());
        var scope = new So6120ChallengeScope(@"\\?\hid#vid_1532&pid_02e0&mi_03&col01#synthetic", "4.01", new(1, 4),
            So6120SupervisedAdmission.DriverHash, new('E', 64), new('F', 64));
        var residuals = So6120AcceptedResiduals.UnnamedFileHandles | So6120AcceptedResiduals.UncoveredAcpiPaths
            | So6120AcceptedResiduals.UnobservedWriters;
        binding = new(identity, 1, new(stamp, Guid.NewGuid(), new('C', 64), scope, scope, residuals, residuals),
            new(stamp, So6120HandoffOrigin.AlreadyStopped, null));
    }

    public int PortCalls => port.Calls;
    // Durable fixture setup must not consume the guardian's semantic deadlines.
    // IPC, gate and process deadlock guards still use real elapsed time.
    public TimeProvider Clock => clock;

    public So6120DirectDutySession CreateSession() =>
        So6120DirectDutySession.CreateInjected(root, binding, port, new EvidenceSource(binding, clock), clock);

    public So6120GuardianTrial Prepare(Action<So6120SilentTrialProgress> progress)
    {
        var session = CreateSession();
        try
        {
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64)));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // External test coordination must not consume the shared logical session,
    // guardian or signal budgets. Only IPC/process deadlock guards use real time.
    private sealed class SessionClock(Func<bool>? waitBeforeCoast) : TimeProvider
    {
        private bool coastReached;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => TimeSpan.TicksPerHour;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // ExecuteStopStage starts the first coast delay after recording
            // both confirmed zero writes and counting the first coast sample.
            if (waitBeforeCoast is not null && !coastReached && dueTime == TimeSpan.FromMilliseconds(500))
            {
                coastReached = true;
                if (!waitBeforeCoast()) { throw new System.TimeoutException("The fake zero checkpoint was not released."); }
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class EvidenceSource(So6120SessionBinding binding, TimeProvider clock) : ISo6120SessionEvidenceSource
    {
        public So6120SessionEvidence Capture()
        {
            long timestamp = clock.GetTimestamp();
            var stamp = new So6120AdmissionStamp(binding.Identity.Owner.OperationId, 1, clock, timestamp);
            var metadata = new So6120SupervisedMetadata(stamp, timestamp, Architecture.X64,
                new(new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "4.01"), null),
                new(1, 4), [new(binding.Challenge.ObservedScope.HidPath, new(0x1532, 0x02E0, 1, 2, 91))],
                new(true, 1, 1, 1),
                new(@"C:\Windows", @"\SystemRoot\System32\Drivers\inpoutx64.sys", @"C:\Windows\System32\Drivers\inpoutx64.sys",
                    ServiceType.KernelDriver, ServiceControllerStatus.Running, 15008, So6120SupervisedAdmission.DriverHash,
                    @"\Device\inpoutx64"),
                [], ServiceControllerStatus.Stopped, []);
            return new(binding.Identity, metadata, binding.Challenge, binding.Handoff);
        }
    }

    private sealed class MemoryPort : ISo6120MailboxBytePort, IDisposable
    {
        private readonly string root;
        private readonly string gate;
        private readonly byte[] mapped = new byte[256];
        private readonly byte[] mailbox = new byte[256];
        private byte selected;
        private int commands;
        private bool disposed;

        public MemoryPort(string root, string gate)
        {
            this.root = root;
            this.gate = gate;
            mapped[0xF0] = 0x11;
            mapped[0xB8] = 10;
            mapped[0xB9] = 20;
            mapped[0xB2] = 183;
            mapped[0xB0] = 50;
            mapped[0xB1] = 60;
            mapped[0x06] = 1;
        }

        public int Calls { get; private set; }

        public So6120PortResult SelectIndex(byte index)
        {
            CountCall();
            selected = index;
            return Done();
        }

        public So6120PortResult ReadData()
        {
            CountCall();
            return new(true, 1, mailbox[selected], 0, So6120PortEffect.Performed);
        }

        public So6120PortResult WriteData(byte value)
        {
            CountCall();
            mailbox[selected] = value;
            if (selected != 0x93) { return Done(); }

            byte register = mailbox[0x91];
            byte data = mailbox[0xA0];
            string command = value == 0xA1 ? $"{value:X2} {register:X2} {data:X2}" : $"{value:X2} {register:X2}";
            File.AppendAllText(Path.Combine(root, "commands.log"), command + "\n");
            commands++;
            if (value == 0xA1)
            {
                mapped[register] = data;
                if (register == 0x1A && data == 0xFE)
                {
                    mapped[0xB0] = mapped[0xB8];
                    mapped[0xB1] = mapped[0xB9];
                    mapped[0x08] = mapped[0x09] = 38;
                    // The command took effect but its result is still pending.
                    if (gate == "fe-pending" && !WaitAtGate())
                    { return new(false, 0, 0, 1460, So6120PortEffect.MayHavePerformed); }
                }
                if (mapped[0x1A] == 0xFE && data == 0)
                {
                    if (register == 0xB8) { mapped[0xB0] = mapped[0x08] = 0; }
                    if (register == 0xB9) { mapped[0xB1] = mapped[0x09] = 0; }
                }
            }
            else { mailbox[0xA0] = mapped[register]; }

            return Finish();
        }

        public bool WaitAtGate()
        {
            Program.WriteReport(root, "pending.json", new PendingReport(Environment.ProcessId, commands, gate));
            var wait = Stopwatch.StartNew();
            string release = Path.Combine(root, "release");
            while (!File.Exists(release))
            {
                if (wait.Elapsed >= TimeSpan.FromMinutes(2))
                { return false; }
                Thread.Sleep(10);
            }

            return true;
        }

        private So6120PortResult Finish()
        {
            mailbox[0x94] = 0x80;
            mailbox[0x93] = 0;
            return Done();
        }

        private void CountCall()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Calls++;
        }

        private static So6120PortResult Done() => new(true, 10, 0, 0, So6120PortEffect.Performed);

        public void Dispose()
        {
            disposed = true;
            // A failed CreateSession on reuse never touches the original run artifacts.
            if (Calls > 0)
            { Program.WriteReport(root, "disposed.json", Environment.ProcessId); }
        }
    }
}
