using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Core.Native;
using OpenBlade.Protocol;

namespace OpenBlade.NativeFailureProbe;

// Each scenario runs in a fresh process. Disposing a real product helper is an
// existing lifetime-failure seam, not a product fallback or test-selected DLL.
// All HID handles and I/O are inert test doubles, as in the Protocol unit tests.
internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task Main(string[] args)
    {
        if (args is not [var scenario] || scenario is not ("batch-encode" or "query-validate" or "policy-failure"))
        {
            throw new ArgumentException("Choose batch-encode, query-validate or policy-failure.");
        }
        ProductNativeRuntime.EnsureAvailable();
        if (scenario == "policy-failure")
        {
            RunPolicyFailure();
            return;
        }
        await RunAsync(scenario);
    }

    private static void RunPolicyFailure()
    {
        // Reflection reaches only the pure internal helper. Service.Program,
        // dependency injection and hardware/session ownership are never started.
        Type resolution = typeof(OpenBlade.Service.ProfileAutomationState).Assembly
            .GetType("OpenBlade.Service.ApplicationProfileResolution", throwOnError: true)!;
        MethodInfo method = resolution.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        var apply = method.CreateDelegate<Func<PowerProfile, ApplicationPowerProfileOverride?, PowerProfile>>();
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        ProductNativeRuntime.Policy.Dispose();

        Require(ReferenceEquals(baseline, apply(baseline, null)),
            "Null override must retain the baseline without touching native policy.");
        var invalidLegacy = new ApplicationPowerProfileOverride { Fans = (FanMode)999 };
        ArgumentOutOfRangeException expected = Expect<ArgumentOutOfRangeException>(
            () => FanControlSettings.FromLegacy((FanMode)999));
        ArgumentOutOfRangeException legacy = Expect<ArgumentOutOfRangeException>(
            () => apply(baseline, invalidLegacy));
        Require(legacy.ParamName == "mode" && legacy.ParamName == expected.ParamName
            && Equals(legacy.ActualValue, expected.ActualValue) && legacy.Message == expected.Message,
            "Invalid legacy fan mode lost its original host exception before native admission.");

        PowerProfile? published = null;
        NativeComponentException failure = Expect<NativeComponentException>(
            () => published = apply(baseline, new()));
        Require(published is null, "A failed native policy operation published a profile.");
        Require(failure.Category == (int)NativeBoundaryErrorKind.Disposed,
            "Native policy lifetime failure lost its typed category.");
        Require(failure.InnerException is null
            && !failure.Message.Contains((char)92) && !failure.Message.Contains('/')
            && !failure.Message.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "Native policy failure exposed a filesystem path.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scenario = "policy-failure",
            passed = true,
            nullOverridePreserved = true,
            legacyException = legacy.GetType().Name,
            legacy.ParamName,
            failure = failure.GetType().Name,
            failure.Category,
            profilePublished = published is not null,
        }));
    }

    private static T Expect<T>(Action operation) where T : Exception
    {
        try
        {
            operation();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task RunAsync(string scenario)
    {
        var cleanup = new List<string>();
        using var cleanupRelease = new ManualResetEventSlim();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ValidationRunContext();
        Require(context.TryAcquireActor(out ValidationRunContext.ActorLease lease), "Initial actor lease.");
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        var handle = new OpenedHidHandle(file, bound,
            () =>
            {
                cleanup.Add("bound");
                cleanupStarted.TrySetResult();
                Require(cleanupRelease.Wait(Timeout), "Cleanup release timed out.");
            },
            () =>
            {
                Require(!context.TryAcquireActor(out _), "Lease must outlive both handle cleanup callbacks.");
                cleanup.Add("file");
            });
        var io = new ControlledIo();
        var actor = new WindowsRazerHidSession(handle, io, new AllowGuard(), TimeProvider.System,
            lease, ControllerExclusivityPolicy.Strict);
        int activeAssignments = 0;
        int queuedAssignments = 0;
        try
        {
            TransactionIdAssignment Assign() => TransactionIdAssignment.Valid(
                checked((byte)Interlocked.Increment(ref activeAssignments)));
            var command = new RazerCommandSpec(0x03, 0x0B, [0xFF, 0x00], 2);
            Task<HidSessionResult<ReadOnlyMemory<byte>>> active = scenario == "batch-encode"
                ? actor.SubmitBatchAsync([command, command, command], Assign, Timeout, default)
                : actor.ExecuteAsync(command, Assign, Timeout, default);
            await io.FirstSetEntered.Task.WaitAsync(Timeout);

            // The first SET buffer came from the actual admitted Rust codec.
            byte[] submitted = io.FirstSet!;
            Require(submitted.Length == 91 && submitted[1] == 0 && submitted[2] == 1,
                "Expected the first native-encoded request.");
            Require(submitted[7] == 0x03 && submitted[8] == 0x0B
                && submitted[9] == 0xFF, "Expected command and payload in native encoding.");

            Task<HidSessionResult<ReadOnlyMemory<byte>>> queued = actor.SubmitAsync(command,
                () =>
                {
                    Interlocked.Increment(ref queuedAssignments);
                    return TransactionIdAssignment.Valid(9);
                }, Timeout, default);
            ProductNativeRuntime.Codec.Dispose();
            io.ReleaseFirstSet.TrySetResult();

            HidSessionResult<ReadOnlyMemory<byte>> result = await active.WaitAsync(Timeout);
            HidSessionResult<ReadOnlyMemory<byte>> drained = await queued.WaitAsync(Timeout);
            await cleanupStarted.Task.WaitAsync(Timeout);
            Require(result.Failure == HidSessionFailureKind.NativeIoFailure, "Claimed item did not complete with native failure.");
            Require(result.Diagnostic?.Phase == HidSessionFailurePhase.SetCompletion,
                "Possible submission must remain uncertain.");
            Require(result.Diagnostic?.ProtocolValidationFailure is null,
                "Codec failure must not become a device-response validation failure.");
            Require(drained.Failure == HidSessionFailureKind.Poisoned, "Queued work was not poisoned.");
            Require(queuedAssignments == 0, "Drained work consumed a transaction ID.");
            Require(activeAssignments == (scenario == "batch-encode" ? 2 : 1),
                "Execution continued beyond the failing codec operation.");
            Require(actor.Disposition == RazerCommandSessionDisposition.RecoveryRequired,
                "Poison must be observable before cleanup completes.");
            Require(io.SetCalls == 1 && io.GetCalls == (scenario == "query-validate" ? 1 : 0),
                "A failed codec operation replayed or advanced I/O.");
            HidSessionResult<ReadOnlyMemory<byte>> rejected = await actor.SubmitAsync(command,
                () => throw new InvalidOperationException("Poisoned actor assigned a new ID."), Timeout, default);
            Require(rejected.Failure == HidSessionFailureKind.Poisoned, "Poisoned actor admitted new work.");

            cleanupRelease.Set();
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout);
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout);
            Require(actor.Disposition == RazerCommandSessionDisposition.FullyDisposed, "Actor did not finish cleanup.");
            Require(cleanup.SequenceEqual(["bound", "file"]), "Handle cleanup repeated or changed order.");
            Require(context.TryAcquireActor(out ValidationRunContext.ActorLease next), "Actor lease was not released.");
            next.Dispose();
            cleanup.Add("lease");
            Require(io.SetCalls == 1 && io.GetCalls == (scenario == "query-validate" ? 1 : 0),
                "Cleanup replayed I/O.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                scenario,
                passed = true,
                claimed = result.Failure.ToString(),
                queued = drained.Failure.ToString(),
                io.SetCalls,
                io.GetCalls,
                activeAssignments,
                queuedAssignments,
                cleanup,
            }));
        }
        finally
        {
            io.ReleaseFirstSet.TrySetResult();
            cleanupRelease.Set();
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class AllowGuard : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], null);
    }

    private sealed class ControlledIo : IHidFeatureIo
    {
        internal TaskCompletionSource FirstSetEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? FirstSet { get; private set; }
        internal int SetCalls { get; private set; }
        internal int GetCalls { get; private set; }

        public async Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint controlCode,
            byte[] buffer, CancellationToken callerCancellation, CancellationToken deadlineCancellation)
        {
            if (SetCalls == 0)
            {
                Require(controlCode == 0x000B0191, "First I/O must be SET_FEATURE.");
                SetCalls++;
                FirstSet = buffer.ToArray();
                FirstSetEntered.TrySetResult();
                await ReleaseFirstSet.Task.WaitAsync(Timeout);
            }
            else
            {
                Require(controlCode == 0x000B0192, "No SET replay or extra batch command is allowed.");
                GetCalls++;
                Require(GetCalls == 1, "Unexpected I/O after the first response.");
                // Construct a successful response without calling the disposed codec.
                FirstSet!.CopyTo(buffer, 0);
                buffer[1] = 2;
            }
            return new(new NativeIoCompletion(true, 91, 0), true, HidFeatureTermination.Completed);
        }
    }
}
