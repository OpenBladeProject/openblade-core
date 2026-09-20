using OpenBlade.Contracts;

namespace OpenBlade.Tray;

[Flags]
internal enum FanControlEditorChanges
{
    None = 0,
    FixedSpeed = 1,
    UseHigherTemperature = 2,
    CpuPoints = 4,
    GpuPoints = 8,
}

// Editor values only. The caller owns fan-mode intent, command admission, and
// the active device range; retaining these values never authorizes a write.
internal sealed class FanControlEditorState
{
    public int FixedSpeedRpm { get; private set; } = FanControlSettings.Default.FixedSpeedRpm;

    public bool UseHigherTemperature { get; private set; } = true;

    public IReadOnlyList<FanCurveEditorPoint> CpuPoints { get; private set; } =
        CreatePoints(ManualFanCurve.Default.Cpu, FanSpeedRange.Standard);

    public IReadOnlyList<FanCurveEditorPoint> GpuPoints { get; private set; } =
        CreatePoints(ManualFanCurve.Default.Gpu, FanSpeedRange.Standard);

    public FanControlSettings ToSettings(FanMode mode) => new(
        mode,
        FixedSpeedRpm,
        new ManualFanCurve(
            CpuPoints.Select(point => point.ToValue()).ToArray(),
            GpuPoints.Select(point => point.ToValue()).ToArray(),
            UseHigherTemperature));

    public FanControlEditorChanges SetFixedSpeed(int speedRpm, FanSpeedRange range)
    {
        int normalized = range.Quantize(speedRpm);
        if (FixedSpeedRpm == normalized)
        {
            return FanControlEditorChanges.None;
        }

        FixedSpeedRpm = normalized;
        return FanControlEditorChanges.FixedSpeed;
    }

    public FanControlEditorChanges SetUseHigherTemperature(bool value)
    {
        if (UseHigherTemperature == value)
        {
            return FanControlEditorChanges.None;
        }

        UseHigherTemperature = value;
        return FanControlEditorChanges.UseHigherTemperature;
    }

    public FanControlEditorChanges Load(FanControlSettings value, FanSpeedRange range)
    {
        FanControlEditorChanges changes = SetFixedSpeed(value.FixedSpeedRpm, range)
            | SetUseHigherTemperature(value.ManualCurve.UseHigherTemperatureForBoth);
        // Preserve bound points on equivalent refreshes, including when raw
        // incoming values differ but normalize to the same device range.
        if (!PointsMatch(CpuPoints, value.ManualCurve.Cpu, range))
        {
            CpuPoints = CreatePoints(value.ManualCurve.Cpu, range);
            changes |= FanControlEditorChanges.CpuPoints;
        }
        if (!PointsMatch(GpuPoints, value.ManualCurve.Gpu, range))
        {
            GpuPoints = CreatePoints(value.ManualCurve.Gpu, range);
            changes |= FanControlEditorChanges.GpuPoints;
        }

        return changes;
    }

    public FanControlEditorChanges UpdatePoint(
        FanCurveEditorPoint point,
        int speedRpm,
        FanSpeedRange range)
    {
        ArgumentNullException.ThrowIfNull(point);
        IReadOnlyList<FanCurveEditorPoint> source = CpuPoints.Contains(point)
            ? CpuPoints
            : GpuPoints.Contains(point)
                ? GpuPoints
                : throw new ArgumentException(
                    "The fan-curve point does not belong to the active editor.",
                    nameof(point));
        int index = source.ToList().IndexOf(point);
        int minimum = index == 0 ? range.MinimumRpm : source[index - 1].SpeedRpm;
        int maximum = index == source.Count - 1 ? range.MaximumRpm : source[index + 1].SpeedRpm;
        point.SpeedRpm = Math.Clamp(range.Quantize(speedRpm), minimum, maximum);
        // Republish the edited side even if clamping leaves its value unchanged:
        // the slider must reflect the accepted value after a user edit.
        FanCurveEditorPoint[] updated = source
            .Select(value => new FanCurveEditorPoint(value.ToValue()))
            .ToArray();
        if (ReferenceEquals(source, CpuPoints))
        {
            CpuPoints = updated;
            return FanControlEditorChanges.CpuPoints;
        }

        GpuPoints = updated;
        return FanControlEditorChanges.GpuPoints;
    }

    private static FanCurveEditorPoint[] CreatePoints(
        IReadOnlyList<FanCurvePoint> points,
        FanSpeedRange range) => points
            .Select(point => new FanCurveEditorPoint(new FanCurvePoint(
                point.TemperatureCelsius,
                range.Quantize(point.SpeedRpm))))
            .ToArray();

    private static bool PointsMatch(
        IReadOnlyList<FanCurveEditorPoint> current,
        IReadOnlyList<FanCurvePoint> incoming,
        FanSpeedRange range)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (current[index].TemperatureCelsius != incoming[index].TemperatureCelsius
                || current[index].SpeedRpm != range.Quantize(incoming[index].SpeedRpm))
            {
                return false;
            }
        }

        return true;
    }
}
