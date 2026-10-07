using System.Diagnostics;
using System.Management;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Implementations independently attest the
// exact source, loaded lifetime and continuous fresh getter admission. This
// scope does not admit requested-state interpretation, range units or setters.
internal abstract class CpuLivePowerRangeTrustedScope
{
    internal abstract bool IsCurrent(CpuLivePowerContext exactContext);
}
internal interface IWindowsCpuPowerRangeSession : IDisposable
{
    IWindowsCpuPowerRangeObject? QueryVersion(Action checkpoint, Action invocationEntered);
    IWindowsCpuPowerRangeObject? GetPowerMinMax(Action checkpoint, Action invocationEntered);
}
internal interface IWindowsCpuPowerRangeObject : IDisposable
{
    IEnumerable<string> Names { get; }
    object? Get(string name);
}

// An opening factory must explicitly account for resources it acquired before
// it could return its session. Any unclassified opening exception is uncertain.
internal sealed class CpuLivePowerRangeOpenFailure(Exception inner, bool cleanupComplete)
    : Exception("Power range opening failed.", inner)
{
    internal bool CleanupComplete { get; } = cleanupComplete;
}

// Synchronous, fixed getters only. Call under the retained Service actual worker;
// WMI timeout options do not mean a cancelled caller has finished native work.
// The PM engine orders version7 before MinMax. Each result is candidate metadata,
// never a trusted range or proof. The shared installed provider constructs this
// reader only within an initiating request's continuously checked context.
internal sealed class WindowsCpuLivePowerRangeMetadata : IRz090528CpuLivePowerRangeMetadataReader
{
    private readonly CpuLivePowerContext? context;
    private readonly CpuLivePowerRangeTrustedScope? scope;
    private readonly Func<Action, IWindowsCpuPowerRangeSession> open;
    private readonly CancellationToken token;

    internal WindowsCpuLivePowerRangeMetadata(CpuLivePowerContext? context, CpuLivePowerRangeTrustedScope? scope,
        CancellationToken token = default) : this(context, scope, NativeSession.Open, token) { }
    internal WindowsCpuLivePowerRangeMetadata(CpuLivePowerContext? context, CpuLivePowerRangeTrustedScope? scope,
        Func<Action, IWindowsCpuPowerRangeSession> open, CancellationToken token = default)
    { this.context = context; this.scope = scope; this.open = open; this.token = token; }

    private readonly object sequenceGate = new();
    private bool completedVersion;
    public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint)
    {
        lock (sequenceGate)
        {
            completedVersion = false;
            var result = Read(static (session, check, entered) => session.QueryVersion(check, entered), DecodeVersion, checkpoint);
            completedVersion = result.Value?.Version == 7 && LocallyComplete(result);
            return result;
        }
    }
    public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint)
    {
        lock (sequenceGate)
        {
            bool eligible = completedVersion;
            completedVersion = false; // Consume before opening or invoking, including failed calls.
            var result = Read(static (session, check, entered) => session.GetPowerMinMax(check, entered),
                (output, check, decoded) => DecodeMinMax(output, check, decoded, eligible), checkpoint);
            return result.Value is { MissingReturnValueCompatibility: true } && !LocallyComplete(result)
                ? result with { Value = null } : result;
        }
    }
    public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint)
    {
        lock (sequenceGate)
        {
            completedVersion = false;
            throw new NotSupportedException("The continuous PPT 50001 profile has no discrete getter.");
        }
    }
    // Local checkpoints and actual cleanup only. The engine separately stamps
    // PostAdmissionPassed after fresh session admission; this never stamps it.
    private static bool LocallyComplete<T>(CpuLivePowerRangeMetadataCall<T> result) where T : class =>
        result.Status == CpuLivePowerRangeMetadataStatus.Complete && result.Stages ==
        (CpuLivePowerRangeMetadataStages.InvocationEntered | CpuLivePowerRangeMetadataStages.InvocationReturned
        | CpuLivePowerRangeMetadataStages.OutputEnumerated | CpuLivePowerRangeMetadataStages.PropertiesRead
        | CpuLivePowerRangeMetadataStages.Disposed);

    private CpuLivePowerRangeMetadataCall<T> Read<T>(
        Func<IWindowsCpuPowerRangeSession, Action, Action, IWindowsCpuPowerRangeObject?> invoke,
        Func<IWindowsCpuPowerRangeObject, Action, Decoded, T?> decode, Action checkpoint) where T : class
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        void Check()
        {
            try
            {
                token.ThrowIfCancellationRequested(); checkpoint();
                if (context is not { IsWellFormed: true }
                    || scope is null || !scope.IsCurrent(context)) { throw new AdmissionFailure(); }
            }
            catch (Exception) { throw new AdmissionFailure(); }
        }
        var decoded = new Decoded(); T? value = null;
        var status = CpuLivePowerRangeMetadataStatus.InvalidData;
        var stages = CpuLivePowerRangeMetadataStages.None;
        DateTimeOffset utc = DateTimeOffset.UtcNow; long timestamp = Stopwatch.GetTimestamp();
        int? hresult = null; bool cleanup = true, opening = false;
        IWindowsCpuPowerRangeSession? session = null; IWindowsCpuPowerRangeObject? output = null;
        try
        {
            Check(); opening = true;
            session = open(Check) ?? throw new InvalidDataException("Missing power range session.");
            opening = false; Check();
            output = invoke(session, Check, () => stages |= CpuLivePowerRangeMetadataStages.InvocationEntered);
            stages |= CpuLivePowerRangeMetadataStages.InvocationReturned;
            utc = DateTimeOffset.UtcNow; timestamp = Stopwatch.GetTimestamp();
            Check();
            if (output is not null) { value = decode(output, Check, decoded); }
            Check(); status = value is null ? CpuLivePowerRangeMetadataStatus.InvalidData : CpuLivePowerRangeMetadataStatus.Complete;
        }
        catch (CpuLivePowerRangeOpenFailure failure)
        { cleanup = failure.CleanupComplete; status = CpuLivePowerRangeMetadataStatus.NativeFailure; hresult = failure.InnerException?.HResult ?? failure.HResult; }
        catch (Exception failure)
        { cleanup &= !opening; status = CpuLivePowerRangeMetadataStatus.NativeFailure; hresult = failure.HResult; }
        finally
        {
            Dispose(output); Dispose(session);
            // Includes a proven pre-open denial (there were no resources), but
            // never infers clean partial Open from absent invocation flags.
            if (cleanup) { stages |= CpuLivePowerRangeMetadataStages.Disposed; }
            else { status = CpuLivePowerRangeMetadataStatus.DisposalFailed; }
        }
        return new(value, status, stages | decoded.Stages, utc, timestamp, hresult, Raw: decoded.Raw);

        void CheckCleanup()
        {
            try { Check(); }
            catch (AdmissionFailure failure)
            { status = CpuLivePowerRangeMetadataStatus.NativeFailure; hresult ??= failure.HResult; }
        }
        void Dispose(IDisposable? resource)
        {
            if (resource is null) { return; }
            CheckCleanup();
            try { resource.Dispose(); }
            catch (AdmissionFailure failure)
            { status = CpuLivePowerRangeMetadataStatus.NativeFailure; hresult ??= failure.HResult; }
            catch (Exception failure) { cleanup = false; hresult = failure.HResult; }
            CheckCleanup();
        }
    }

    private sealed class Decoded
    {
        internal CpuLivePowerRangeMetadataStages Stages;
        internal CpuLivePowerRangeMetadataRaw Raw = new();
    }
    private static CpuLivePowerRangeVersion? DecodeVersion(IWindowsCpuPowerRangeObject output, Action check, Decoded decoded)
    {
        var fields = Fields(output, ["version", "ReturnValue"], check, decoded, final: true);
        // Complete strict enumeration alone can prove absence. Every checkpoint
        // also requires exact hardware and independent source trust.
        return fields is not null && fields.TryGetValue("version", out var value) && value is uint version && version == 7
            && (decoded.Raw.ReturnValueState == CpuLivePowerRangeReturnValueState.Absent
                || decoded.Raw is { ReturnValueState: CpuLivePowerRangeReturnValueState.PresentBoolean, ReturnValue: true })
            ? new(version, decoded.Raw.ReturnValueState == CpuLivePowerRangeReturnValueState.Absent) : null;
    }
    private static CpuLivePowerRangeMinMax? DecodeMinMax(IWindowsCpuPowerRangeObject output, Action check, Decoded decoded, bool completedVersion)
    {
        var header = Fields(output, ["ReturnValue", "pack"], check, decoded, final: false);
        if (header is null || !header.TryGetValue("pack", out var embedded) || embedded is not IWindowsCpuPowerRangeObject pack) { return null; }
        // Decode candidates even with a declined acknowledgement to retain raw
        // evidence. The owning output must dispose all embedded objects.
        var fields = Fields(pack, ["Current", "Min", "Max", "Stepping", "Value"], check, decoded, final: true);
        if (fields is null || fields.Count != 5 || decoded.Raw is not
            { Current: uint current, Min: uint min, Max: uint max, Stepping: uint step, Value: 0x50001 }) { return null; }
        bool compatible = completedVersion && decoded.Raw.ReturnValueState == CpuLivePowerRangeReturnValueState.Absent
            && min <= max && step > 0 && current >= min && current <= max;
        return decoded.Raw is { ReturnValueState: CpuLivePowerRangeReturnValueState.PresentBoolean, ReturnValue: true }
            || compatible ? new(0x50001, current, min, max, step, compatible) : null;
        // Geometry only constrains missing-ACK compatibility. Current is a shadow;
        // neither units nor requested state are established by these candidates.
    }
    private static Dictionary<string, object?>? Fields(IWindowsCpuPowerRangeObject output, string[] allowed,
        Action check, Decoded decoded, bool final)
    {
        var names = new List<string>(); check();
        foreach (string name in output.Names)
        {
            check();
            if (names.Count >= allowed.Length || !allowed.Contains(name, StringComparer.Ordinal)
                || names.Contains(name, StringComparer.Ordinal)) { return null; }
            names.Add(name);
        }
        if (final) { decoded.Stages |= CpuLivePowerRangeMetadataStages.OutputEnumerated; }
        if (allowed.Contains("ReturnValue", StringComparer.Ordinal) && !names.Contains("ReturnValue", StringComparer.Ordinal))
        { Record(decoded, "ReturnValue", null, CpuLivePowerRangeReturnValueState.Absent); }
        check();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            check(); object? value;
            try { value = output.Get(name); }
            catch { if (name == "ReturnValue") { Record(decoded, name, null, CpuLivePowerRangeReturnValueState.Unreadable); } throw; }
            values.Add(name, value); Record(decoded, name, value); check();
        }
        if (final) { decoded.Stages |= CpuLivePowerRangeMetadataStages.PropertiesRead; }
        return values;
    }
    private static void Record(Decoded decoded, string name, object? value, CpuLivePowerRangeReturnValueState? ack = null)
    {
        var old = decoded.Raw; uint? word = value is uint number ? number : null;
        decoded.Raw = new(
            returnValue: name == "ReturnValue" ? value as bool? : old.ReturnValue,
            version: name == "version" ? word : old.Version,
            current: name == "Current" ? word : old.Current,
            min: name == "Min" ? word : old.Min,
            max: name == "Max" ? word : old.Max,
            stepping: name == "Stepping" ? word : old.Stepping,
            value: name == "Value" ? word : old.Value,
            returnValueState: name == "ReturnValue" ? ack ?? (value is bool ? CpuLivePowerRangeReturnValueState.PresentBoolean
                : CpuLivePowerRangeReturnValueState.WrongType) : old.ReturnValueState);
    }
    private sealed class AdmissionFailure : Exception;

    // The two failure channels must remain separate: admission revocation cannot
    // hide a later child/resource disposal failure, including cleanup OCE.
    private static void DisposeAll(IEnumerable<IDisposable> resources, Action check)
    {
        bool revoked = false; Exception? cleanupFailure = null;
        foreach (var resource in resources)
        {
            try { check(); } catch (AdmissionFailure) { revoked = true; }
            try { resource.Dispose(); }
            catch (AdmissionFailure) { revoked = true; }
            catch (Exception failure) { cleanupFailure ??= failure; }
            try { check(); } catch (AdmissionFailure) { revoked = true; }
        }
        if (cleanupFailure is not null) { throw cleanupFailure; }
        if (revoked) { throw new AdmissionFailure(); }
    }
    private sealed class NativeObject(ManagementBaseObject source, Action check) : IWindowsCpuPowerRangeObject
    {
        private readonly List<NativeObject> children = [];
        public IEnumerable<string> Names { get { foreach (PropertyData property in source.Properties) { yield return property.Name; } } }
        public object? Get(string name)
        {
            object? value = source[name];
            if (value is not ManagementBaseObject embedded) { return value; }
            var child = new NativeObject(embedded, check); children.Add(child); return child;
        }
        public void Dispose() => DisposeAll(children.Cast<IDisposable>().Append(source), check);
    }
    private sealed class NativeSession : IWindowsCpuPowerRangeSession
    {
        private readonly List<IDisposable> resources = [];
        private ManagementObject instance = null!;
        private Action check = null!;
        internal static IWindowsCpuPowerRangeSession Open(Action check)
        {
            var session = new NativeSession { check = check };
            try
            {
                check();
                var search = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"), new ObjectQuery("SELECT * FROM AMD_ACPI"),
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
                session.resources.Add(search); check();
                var objects = search.Get(); session.resources.Add(objects); check();
                int count = 0;
                foreach (ManagementObject candidate in objects)
                {
                    session.resources.Add(candidate); check();
                    if (++count > 1) { throw new InvalidDataException("Unexpected power provider count."); }
                    session.instance = candidate;
                }
                check(); if (count != 1) { throw new InvalidDataException("Missing power provider."); }
                return session;
            }
            catch (Exception failure)
            {
                try { session.Dispose(); }
                catch (AdmissionFailure) { throw new CpuLivePowerRangeOpenFailure(failure, true); }
                catch (Exception cleanup) { throw new CpuLivePowerRangeOpenFailure(cleanup, false); }
                throw new CpuLivePowerRangeOpenFailure(failure, true);
            }
        }
        public IWindowsCpuPowerRangeObject? QueryVersion(Action checkpoint, Action invocationEntered)
        {
            checkpoint(); invocationEntered();
            var output = instance.InvokeMethod("QueryVersion", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
            return output is null ? null : new NativeObject(output, checkpoint);
        }
        public IWindowsCpuPowerRangeObject? GetPowerMinMax(Action checkpoint, Action invocationEntered)
        {
            checkpoint(); var input = instance.GetMethodParameters("GetMinMaxSteppingValues"); resources.Add(input); checkpoint();
            input["ID"] = (uint)0x50001; checkpoint(); invocationEntered();
            var output = instance.InvokeMethod("GetMinMaxSteppingValues", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
            // Transfer ownership before a post-invocation checkpoint can fail.
            return output is null ? null : new NativeObject(output, checkpoint);
        }
        public void Dispose()
        { try { DisposeAll(resources.AsEnumerable().Reverse(), check); } finally { resources.Clear(); } }
    }
}
