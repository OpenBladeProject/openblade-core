using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Developer evidence only: these fixed getters do not establish live-PPT authority.
internal sealed class WindowsCpuLivePowerRangeMetadata : IRz090528CpuLivePowerRangeMetadataReader
{
    private readonly Func<Action, IWindowsCpuRangeWmiSession> open;
    private readonly Func<HostIdentityReadResult>? readIdentity;
    internal WindowsCpuLivePowerRangeMetadata() : this(NativeSession.Open, ReadNativeIdentity) { }
    internal WindowsCpuLivePowerRangeMetadata(Func<Action, IWindowsCpuRangeWmiSession> open,
        Func<HostIdentityReadResult>? readIdentity = null)
    { this.open = open; this.readIdentity = readIdentity; }
    private static HostIdentityReadResult ReadNativeIdentity()
    {
        var registry = new WindowsRegistryValueSource();
        return new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity).ReadExactIdentity();
    }

    private readonly object sequenceGate = new();
    private bool completedVersion;
    public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint)
    {
        lock (sequenceGate)
        {
            completedVersion = false;
            var result = Read("QueryVersion", null, WindowsCpuRangeMetadataDecoder.Version, checkpoint, readIdentity is not null);
            completedVersion = readIdentity is not null && result.Value?.Version == 7 && LocallyComplete(result);
            return result;
        }
    }
    public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint)
    {
        lock (sequenceGate)
        {
            bool eligible = completedVersion;
            completedVersion = false; // Consume before opening or invoking, including failed calls.
            var result = Read("GetMinMaxSteppingValues", 0x50001,
                (output, check) => WindowsCpuRangeMetadataDecoder.MinMax(output, check, eligible), checkpoint, eligible);
            return result.Value is { MissingReturnValueCompatibility: true } && !LocallyComplete(result)
                ? result with { Value = null } : result;
        }
    }
    public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint)
    {
        lock (sequenceGate)
        {
            completedVersion = false;
            return Read("Getdvalues", 0x50001, WindowsCpuRangeMetadataDecoder.DValues, checkpoint);
        }
    }
    // Local checkpoints and actual cleanup only. The engine separately stamps
    // PostAdmissionPassed after fresh session admission; this never stamps it.
    private static bool LocallyComplete<T>(CpuLivePowerRangeMetadataCall<T> result) where T : class =>
        result.Status == CpuLivePowerRangeMetadataStatus.Complete && result.Stages ==
        (CpuLivePowerRangeMetadataStages.InvocationEntered | CpuLivePowerRangeMetadataStages.InvocationReturned
        | CpuLivePowerRangeMetadataStages.OutputEnumerated | CpuLivePowerRangeMetadataStages.PropertiesRead
        | CpuLivePowerRangeMetadataStages.Disposed);

    private CpuLivePowerRangeMetadataCall<T> Read<T>(string method, uint? id,
        Func<IWindowsCpuRangeWmiObject, Action, T?> decode, Action checkpoint, bool requireCompatibilityIdentity = false) where T : class
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        void CheckBoundary()
        {
            try
            {
                checkpoint();
                if (requireCompatibilityIdentity)
                {
                    var identity = readIdentity!();
                    var expected = WindowsHostIdentitySource.Rz090528Identity;
                    if (identity.Failure is not null || identity.Evidence is not { } host
                        || host.Manufacturer != expected.Manufacturer || host.ProductName != expected.ProductName
                        || host.Sku != expected.Sku || host.BiosVersion != "2.02")
                    { throw new InvalidOperationException("Power range profile identity changed."); }
                }
            }
            catch (Exception exception) { throw new BoundaryFailure(exception.HResult); }
        }
        T? value = null;
        var stages = CpuLivePowerRangeMetadataStages.None;
        var status = CpuLivePowerRangeMetadataStatus.InvalidData;
        int? hresult = null;
        var utc = DateTimeOffset.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        IWindowsCpuRangeWmiSession? session = null;
        IWindowsCpuRangeWmiObject? output = null;
        try
        {
            CheckBoundary();
            session = open(CheckBoundary);
            CheckBoundary();
            output = session.Invoke(method, id, CheckBoundary,
                () => stages |= CpuLivePowerRangeMetadataStages.InvocationEntered);
            stages |= CpuLivePowerRangeMetadataStages.InvocationReturned;
            utc = DateTimeOffset.UtcNow; timestamp = Stopwatch.GetTimestamp();
            CheckBoundary();
            if (output is not null)
            {
                value = decode(output, CheckBoundary);
                stages |= output.CompletedStages;
                status = value is null ? CpuLivePowerRangeMetadataStatus.InvalidData : CpuLivePowerRangeMetadataStatus.Complete;
            }
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            status = CpuLivePowerRangeMetadataStatus.NativeFailure;
            hresult = exception.HResult;
        }
        finally
        {
            if (output is not null) { stages |= output.CompletedStages; }
            bool disposed = true;
            Dispose(output); Dispose(session);
            if (disposed && (output is not null || session is not null)) { stages |= CpuLivePowerRangeMetadataStages.Disposed; }

            void Dispose(IDisposable? resource)
            {
                if (resource is null) { return; }
                // A failed admission check cannot skip deterministic cleanup.
                try { CheckBoundary(); }
                catch (Exception exception) when (IsFailure(exception))
                { if (status != CpuLivePowerRangeMetadataStatus.DisposalFailed) { status = CpuLivePowerRangeMetadataStatus.NativeFailure; } hresult ??= exception.HResult; }
                try { resource.Dispose(); }
                catch (BoundaryFailure exception)
                { if (status != CpuLivePowerRangeMetadataStatus.DisposalFailed) { status = CpuLivePowerRangeMetadataStatus.NativeFailure; } hresult ??= exception.HResult; }
                catch (Exception exception) when (IsFailure(exception))
                { disposed = false; status = CpuLivePowerRangeMetadataStatus.DisposalFailed; hresult ??= exception.HResult; }
                try { CheckBoundary(); }
                catch (Exception exception) when (IsFailure(exception))
                { if (status != CpuLivePowerRangeMetadataStatus.DisposalFailed) { status = CpuLivePowerRangeMetadataStatus.NativeFailure; } hresult ??= exception.HResult; }
            }
        }
        return new(value, status, stages, utc, timestamp, hresult, Raw: output?.Raw);
    }

    internal static bool IsFailure(Exception exception) => exception is ManagementException or COMException
        or InvalidOperationException or ArgumentException or UnauthorizedAccessException
        or OperationCanceledException or System.Security.SecurityException or NotSupportedException
        or IOException or BoundaryFailure;
    private sealed class BoundaryFailure : Exception
    {
        internal BoundaryFailure(int hresult) => HResult = hresult;
    }

    private sealed class NativeObject(ManagementBaseObject source, Action checkpoint) : IWindowsCpuRangeWmiObject
    {
        private readonly List<NativeObject> children = [];
        public CpuLivePowerRangeMetadataStages CompletedStages { get; set; }
        public CpuLivePowerRangeMetadataRaw? Raw { get; set; }
        public IEnumerable<string> Names
        { get { foreach (PropertyData property in source.Properties) { yield return property.Name; } } }
        public object? Get(string name)
        {
            object? value = source[name];
            if (value is ManagementBaseObject embedded)
            {
                var child = new NativeObject(embedded, checkpoint); children.Add(child); return child;
            }
            return value;
        }
        public void Dispose()
        {
            Exception? failure = null;
            foreach (var child in children)
            { try { child.Dispose(); } catch (Exception exception) when (IsFailure(exception)) { failure ??= exception; } }
            try { checkpoint(); } catch (Exception exception) when (IsFailure(exception)) { failure ??= exception; }
            try { source.Dispose(); } catch (Exception exception) when (IsFailure(exception)) { if (failure is null || failure is BoundaryFailure) { failure = exception; } }
            try { checkpoint(); } catch (Exception exception) when (IsFailure(exception)) { failure ??= exception; }
            if (failure is not null) { throw failure; }
        }
    }

    private sealed class NativeSession : IWindowsCpuRangeWmiSession
    {
        private readonly List<IDisposable> resources = [];
        private ManagementObject? instance;
        private Action checkpoint = null!;
        internal static IWindowsCpuRangeWmiSession Open(Action checkpoint)
        {
            var session = new NativeSession { checkpoint = checkpoint };
            try
            {
                checkpoint();
                var search = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM AMD_ACPI");
                session.resources.Add(search);
                checkpoint();
                var objects = search.Get(); session.resources.Add(objects);
                checkpoint();
                int count = 0;
                foreach (ManagementObject candidate in objects)
                {
                    session.resources.Add(candidate); count++;
                    checkpoint();
                    if (count > 1) { throw new InvalidOperationException("Unexpected provider count."); }
                    session.instance = candidate;
                }
                checkpoint();
                if (count != 1) { throw new InvalidOperationException("Missing provider."); }
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        public IWindowsCpuRangeWmiObject? Invoke(string method, uint? id, Action check, Action invocationEntered)
        {
            ManagementBaseObject? input = null;
            if (id.HasValue)
            {
                check(); input = instance!.GetMethodParameters(method); resources.Add(input); check();
                input["ID"] = id.Value; check();
            }
            check();
            invocationEntered();
            ManagementBaseObject? result = instance!.InvokeMethod(method, input, null);
            // Caller owns result before any post-invocation checkpoint can fail.
            return result is null ? null : new NativeObject(result, check);
        }
        public void Dispose()
        {
            Exception? failure = null;
            for (int index = resources.Count - 1; index >= 0; index--)
            {
                try { checkpoint(); } catch (Exception exception) when (IsFailure(exception)) { failure ??= exception; }
                try { resources[index].Dispose(); } catch (Exception exception) when (IsFailure(exception)) { if (failure is null || failure is BoundaryFailure) { failure = exception; } }
                try { checkpoint(); } catch (Exception exception) when (IsFailure(exception)) { failure ??= exception; }
            }
            resources.Clear();
            if (failure is not null) { throw failure; }
        }
    }
}

internal interface IWindowsCpuRangeWmiSession : IDisposable
{
    IWindowsCpuRangeWmiObject? Invoke(string method, uint? id, Action checkpoint, Action invocationEntered);
}
internal interface IWindowsCpuRangeWmiObject : IDisposable
{
    IEnumerable<string> Names { get; }
    object? Get(string name);
    CpuLivePowerRangeMetadataStages CompletedStages { get; set; }
    CpuLivePowerRangeMetadataRaw? Raw { get; set; }
}

internal static class WindowsCpuRangeMetadataDecoder
{
    internal static CpuLivePowerRangeVersion? Version(IWindowsCpuRangeWmiObject output, Action checkpoint)
    {
        var fields = Fields(output, ["version", "ReturnValue"], ["version"], checkpoint);
        if (fields is null || fields["version"] is not uint version || version != 7
            || fields.TryGetValue("ReturnValue", out var ack) && ack is not true) { return null; }
        return new(version, !fields.ContainsKey("ReturnValue"));
    }
    internal static CpuLivePowerRangeDValues? DValues(IWindowsCpuRangeWmiObject output, Action checkpoint)
    {
        var pack = Package(output, ["Current", "ID", "Length", "DValuesBuffer"], checkpoint);
        if (pack is null || pack["Current"] is not uint current || pack["ID"] is not uint id || id != 0x50001
            || pack["Length"] is not uint length || pack["DValuesBuffer"] is not uint[] values
            || values.GetType() != typeof(uint[])
            || values.Length > 100 || length != values.Length) { return null; }
        return new(id, current, length, values);
    }
    internal static CpuLivePowerRangeMinMax? MinMax(IWindowsCpuRangeWmiObject output, Action checkpoint, bool completedVersion)
    {
        var pack = PackageFields(output, ["Current", "Max", "Min", "Stepping", "Value"], checkpoint);
        if (pack is null || pack["Current"] is not uint current || pack["Max"] is not uint max
            || pack["Min"] is not uint min || pack["Stepping"] is not uint stepping
            || pack["Value"] is not uint value || value != 0x50001) { return null; }
        bool compatible = completedVersion && output.Raw?.ReturnValueState == CpuLivePowerRangeReturnValueState.Absent
            && min <= max && stepping > 0 && current >= min && current <= max;
        return output.Raw is { ReturnValueState: CpuLivePowerRangeReturnValueState.PresentBoolean, ReturnValue: true }
            || compatible ? new(value, current, min, max, stepping, compatible) : null;
    }
    private static Dictionary<string, object?>? Package(IWindowsCpuRangeWmiObject output, string[] expected, Action checkpoint)
    {
        var decoded = PackageFields(output, expected, checkpoint);
        return output.Raw is { ReturnValueState: CpuLivePowerRangeReturnValueState.PresentBoolean, ReturnValue: true }
            ? decoded : null;
    }
    private static Dictionary<string, object?>? PackageFields(IWindowsCpuRangeWmiObject output, string[] expected, Action checkpoint)
    {
        var fields = Fields(output, ["ReturnValue", "pack"], ["pack"], checkpoint);
        if (fields is null || fields["pack"] is not IWindowsCpuRangeWmiObject pack)
        { return null; }
        try
        {
            return Fields(pack, expected, expected, checkpoint);
        }
        finally
        {
            output.CompletedStages |= pack.CompletedStages;
            var raw = pack.Raw;
            if (raw is not null)
            {
                output.Raw = new(output.Raw?.ReturnValue, raw.Version, raw.Id, raw.Current,
                raw.Length, raw.Min, raw.Max, raw.Stepping, raw.Value, raw.CopyDValuesBuffer(),
                output.Raw?.ReturnValueState ?? CpuLivePowerRangeReturnValueState.NotRead);
            }
        }
    }
    private static Dictionary<string, object?>? Fields(IWindowsCpuRangeWmiObject output,
        string[] allowed, string[] required, Action checkpoint)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        checkpoint();
        foreach (string name in output.Names)
        {
            checkpoint();
            if (names.Count >= allowed.Length || !allowed.Contains(name, StringComparer.Ordinal) || !names.Add(name))
            { return null; }
        }
        output.CompletedStages |= CpuLivePowerRangeMetadataStages.OutputEnumerated;
        if (allowed.Contains("ReturnValue", StringComparer.Ordinal) && !names.Contains("ReturnValue"))
        { RecordRaw(output, "ReturnValue", null, CpuLivePowerRangeReturnValueState.Absent); }
        checkpoint();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            checkpoint();
            object? value;
            try { value = output.Get(name); }
            catch
            {
                if (name == "ReturnValue") { RecordRaw(output, name, null, CpuLivePowerRangeReturnValueState.Unreadable); }
                throw;
            }
            values.Add(name, value); RecordRaw(output, name, value); checkpoint();
        }
        output.CompletedStages |= CpuLivePowerRangeMetadataStages.PropertiesRead;
        return required.Any(name => !names.Contains(name)) ? null : values;
    }
    private static void RecordRaw(IWindowsCpuRangeWmiObject output, string name, object? value,
        CpuLivePowerRangeReturnValueState? returnValueState = null)
    {
        var old = output.Raw;
        uint? word = value is uint number ? number : null;
        output.Raw = new(
            name == "ReturnValue" ? value as bool? : old?.ReturnValue,
            name == "version" ? word : old?.Version,
            name == "ID" ? word : old?.Id,
            name == "Current" ? word : old?.Current,
            name == "Length" ? word : old?.Length,
            name == "Min" ? word : old?.Min,
            name == "Max" ? word : old?.Max,
            name == "Stepping" ? word : old?.Stepping,
            name == "Value" ? word : old?.Value,
            name == "DValuesBuffer" ? value is uint[] { Length: <= 100 } array && array.GetType() == typeof(uint[]) ? array : null : old?.CopyDValuesBuffer(),
            name == "ReturnValue" ? returnValueState ?? (value is bool ? CpuLivePowerRangeReturnValueState.PresentBoolean
                : CpuLivePowerRangeReturnValueState.WrongType) : old?.ReturnValueState ?? CpuLivePowerRangeReturnValueState.NotRead);
    }
}
