using OpenBlade.Platform;

namespace OpenBlade.Platform.Tests;

public sealed class WindowsProcessorEfficiencyPolicyTests
{
    [Fact]
    public void HybridTopologySelectsAvailableLowestEfficiencyClassForHiddenThread()
    {
        var api = new FakeProcessorPolicyApi(
            new WindowsCpuSet(40, 0, 0, 8, false, false),
            new WindowsCpuSet(41, 0, 1, 8, false, false),
            new WindowsCpuSet(10, 0, 8, 0, false, false),
            new WindowsCpuSet(11, 0, 9, 0, false, false),
            new WindowsCpuSet(12, 0, 10, 0, true, false),
            new WindowsCpuSet(13, 0, 11, 0, true, true));

        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        Assert.True(policy.Status.HybridTopologyDetected);
        Assert.Equal(3, policy.Status.EfficientLogicalProcessorCount);
        Assert.Equal(2, policy.Status.PerformanceLogicalProcessorCount);
        Assert.True(policy.TryPreferEfficientProcessorsForCurrentThread());
        Assert.NotNull(api.CurrentThreadCpuSets);
        Assert.Equal([10u, 11u, 13u], api.CurrentThreadCpuSets);
    }

    [Fact]
    public void VisibleThreadClearsItsHiddenCpuSetSelection()
    {
        var api = new FakeProcessorPolicyApi(
            new WindowsCpuSet(2, 1, 3, 4, false, false),
            new WindowsCpuSet(1, 0, 7, 0, false, false));
        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        Assert.True(policy.TryPreferEfficientProcessorsForCurrentThread());
        Assert.NotNull(api.CurrentThreadCpuSets);
        Assert.Equal([1u], api.CurrentThreadCpuSets);

        Assert.True(policy.TryAllowAllProcessorsForCurrentThread());
        Assert.True(api.CurrentThreadCpuSetsCleared);
        Assert.Null(api.CurrentThreadCpuSets);
    }

    [Fact]
    public void CurrentThreadFailureIsBestEffort()
    {
        var api = new FakeProcessorPolicyApi(
            new WindowsCpuSet(1, 0, 0, 0, false, false),
            new WindowsCpuSet(2, 0, 1, 1, false, false))
        {
            SetCurrentThreadResult = false,
        };
        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        Assert.False(policy.TryPreferEfficientProcessorsForCurrentThread());
    }

    [Fact]
    public void CpuSetChangesAreRejectedOutsideDiscoveringThread()
    {
        var api = new FakeProcessorPolicyApi(
            new WindowsCpuSet(1, 0, 0, 0, false, false),
            new WindowsCpuSet(2, 0, 1, 1, false, false))
        {
            CurrentThreadId = 42,
        };
        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        api.CurrentThreadId = 84;

        Assert.False(policy.TryPreferEfficientProcessorsForCurrentThread());
        Assert.False(policy.TryAllowAllProcessorsForCurrentThread());
        Assert.Null(api.CurrentThreadCpuSets);
        Assert.False(api.CurrentThreadCpuSetsCleared);

        api.CurrentThreadId = 42;

        Assert.True(policy.TryPreferEfficientProcessorsForCurrentThread());
        Assert.True(policy.TryAllowAllProcessorsForCurrentThread());
    }

    [Fact]
    public void HomogeneousTopologyLeavesWindowsSchedulingAutomatic()
    {
        var api = new FakeProcessorPolicyApi(
            new WindowsCpuSet(1, 0, 0, 0, false, false),
            new WindowsCpuSet(2, 0, 1, 0, false, false));

        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        Assert.False(policy.Status.HybridTopologyDetected);
        Assert.False(policy.TryPreferEfficientProcessorsForCurrentThread());
        Assert.False(policy.TryAllowAllProcessorsForCurrentThread());
        Assert.Null(api.CurrentThreadCpuSets);
        Assert.False(api.CurrentThreadCpuSetsCleared);
        Assert.Contains(
            "EcoQoS is handled independently",
            policy.Status.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableTopologyFallsBackWithoutChangingThreadSelection()
    {
        var api = new FakeProcessorPolicyApi();
        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        Assert.False(policy.Status.HybridTopologyDetected);
        Assert.False(policy.TryPreferEfficientProcessorsForCurrentThread());
        Assert.False(policy.TryAllowAllProcessorsForCurrentThread());
    }

    [Fact]
    public void EcoQosCanBeEnabledAndReturnedToWindowsControl()
    {
        var api = new FakeProcessorPolicyApi(
            new WindowsCpuSet(1, 0, 0, 0, false, false));
        WindowsProcessorEfficiencyPolicy policy =
            WindowsProcessorEfficiencyPolicy.Discover(api);

        Assert.True(policy.TrySetProcessEcoQos(true));
        Assert.True(policy.TrySetProcessEcoQos(false));
        Assert.Equal([true, false], api.EcoQosChanges);
    }

    [Fact]
    public void WindowsTopologyReaderReturnsUniqueCpuSets()
    {
        IReadOnlyList<WindowsCpuSet> cpuSets =
            WindowsProcessorPolicyApi.Instance.GetAvailableCpuSets();

        Assert.NotEmpty(cpuSets);
        Assert.Equal(cpuSets.Count, cpuSets.Select(cpu => cpu.Id).Distinct().Count());
    }

    private sealed class FakeProcessorPolicyApi(params WindowsCpuSet[] cpuSets)
        : IWindowsProcessorPolicyApi
    {
        public bool SetCurrentThreadResult { get; init; } = true;

        public uint CurrentThreadId { get; set; } = 1;

        public uint[]? CurrentThreadCpuSets { get; private set; }

        public bool CurrentThreadCpuSetsCleared { get; private set; }

        public List<bool> EcoQosChanges { get; } = [];

        public uint GetCurrentThreadId() => CurrentThreadId;

        public IReadOnlyList<WindowsCpuSet> GetAvailableCpuSets() => cpuSets;

        public bool SetCurrentThreadCpuSets(uint[] cpuSetIds)
        {
            if (SetCurrentThreadResult)
            {
                CurrentThreadCpuSets = [.. cpuSetIds];
            }
            return SetCurrentThreadResult;
        }

        public bool ClearCurrentThreadCpuSets()
        {
            CurrentThreadCpuSetsCleared = true;
            CurrentThreadCpuSets = null;
            return true;
        }

        public bool SetProcessEcoQos(bool enabled)
        {
            EcoQosChanges.Add(enabled);
            return true;
        }
    }
}
