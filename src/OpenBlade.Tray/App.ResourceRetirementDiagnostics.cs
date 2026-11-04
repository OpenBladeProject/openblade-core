#if OPENBLADE_DEVELOPER_HARNESS
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace OpenBlade.Tray;

public partial class App
{
    // Opt-in observation only. The UI dispatcher stays alive while closed child
    // windows retire; the main window and workload fixture deliberately stay rooted.
    // Never close borrowed framework objects or change production GC behavior.
    private static async Task ObserveResourceRetirementAsync(
        List<ResourceRetirementCheckpoint> checkpoints, int cycles, long started, Process process)
    {
        for (int round = 1; round <= 4; round++)
        {
            await Task.Delay(250);
            CollectResourceRetainedBytes();
            await Task.Delay(250);
            await WaitForResourceDispatcherTurnAsync();
            checkpoints.Add(CaptureResourceRetirementCheckpoint($"drain-{round}", cycles, started, process));
        }
        await Task.Delay(TimeSpan.FromSeconds(10));
        checkpoints.Add(CaptureResourceRetirementCheckpoint("after-retirement-idle", cycles, started, process));
    }

    private static async Task WaitForResourceDispatcherTurnAsync()
    {
        // Confirm one low-priority dispatcher turn, not a composition flush or
        // a promise that no future framework teardown can be scheduled.
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Resource retirement requires the UI dispatcher.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => ready.TrySetResult()))
        {
            throw new InvalidOperationException("The resource retirement dispatcher rejected its checkpoint.");
        }
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ResourceRetirementCheckpoint CaptureResourceRetirementCheckpoint(
        string stage, int cycles, long started, Process process,
        ResourceHandleSnapshot? nativeHandles = null, ResourceGuiSnapshot? guiResources = null)
    {
        // These are separate, sequential observations, not one atomic sample.
        // GCMemoryInfo describes the last GC; private bytes include much more
        // than native allocations and must not be labeled a native heap size.
        GCMemoryInfo managed = GC.GetGCMemoryInfo();
        process.Refresh();
        return new(stage, cycles, Stopwatch.GetElapsedTime(started).TotalSeconds,
            process.HandleCount, process.PrivateMemorySize64, GC.GetTotalMemory(forceFullCollection: false),
            managed.HeapSizeBytes, managed.TotalCommittedBytes, managed.FragmentedBytes,
            managed.Index, managed.Generation, managed.Concurrent,
            nativeHandles ?? CaptureResourceHandleTypes(), guiResources ?? CaptureResourceGuiCounts());
    }

    private sealed record ResourceRetirementCheckpoint(
        string Stage, int DashboardCycles, double ElapsedSeconds,
        int ProcessHandles, long PrivateBytes, long ManagedBytesEstimate,
        long LastGcHeapBytes, long LastGcCommittedBytes, long LastGcFragmentedBytes,
        long LastGcIndex, int LastGcGeneration, bool LastGcConcurrent,
        ResourceHandleSnapshot NativeHandles, ResourceGuiSnapshot GuiResources);
}
#endif
