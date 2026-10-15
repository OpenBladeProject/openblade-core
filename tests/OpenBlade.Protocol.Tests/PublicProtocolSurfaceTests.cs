using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PublicProtocolSurfaceTests
{
    private static readonly Assembly ProtocolAssembly = typeof(TargetBladeDevice).Assembly;

    private static readonly HashSet<string> ForbiddenExportedTypeNames =
    [
        "IRazerTransport",
        "IHidFeatureChannel",
        "SerializedRazerTransport",
        "RazerReport",
        "RazerCommandStatus",
        "BatteryValidationProtocol",
        "DeviceIdentity",
        "CapturedCommandFixture",
        "RazerCommandDefinition",
        "DeviceCommandCatalog",
        "TrustedFixtureHashRegistry",
    ];

    private static readonly HashSet<string> ForbiddenMemberNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "transactionId",
            "commandClass",
            "classId",
            "commandId",
        };

    [Fact]
    public void ProtocolAssemblyDoesNotExportRawImplementationTypes()
    {
        string[] exportedForbiddenTypes = ProtocolAssembly
            .GetExportedTypes()
            .Where(type => ForbiddenExportedTypeNames.Contains(type.Name))
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(exportedForbiddenTypes);
    }

    [Fact]
    public void CapturedFixtureFileExposesOnlyTypedValidation()
    {
        MethodInfo method = Assert.Single(
            typeof(CapturedFixtureFile).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));

        Assert.Equal("ValidateCapturedFixtureAsync", method.Name);
        Assert.Equal(typeof(Task), method.ReturnType);
        ParameterInfo[] parameters = method.GetParameters();
        Assert.Collection(
            parameters,
            path => Assert.Equal(typeof(string), path.ParameterType),
            cancellationToken =>
            {
                Assert.Equal(typeof(CancellationToken), cancellationToken.ParameterType);
                Assert.True(cancellationToken.HasDefaultValue);
            });

        MemberInfo[] unexpectedMembers = typeof(CapturedFixtureFile)
            .GetMembers(BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(member => member is not MethodInfo || member.Name != method.Name)
            .ToArray();
        Assert.Empty(unexpectedMembers);
    }

    [Fact]
    public void DisposableTypedValidationContractsRemainPublic()
    {
        Assert.Contains(typeof(IBatteryValidationRunFactory), ProtocolAssembly.GetExportedTypes());
        Assert.Contains(typeof(IBatteryValidationPort), ProtocolAssembly.GetExportedTypes());
        Assert.Contains(typeof(IBatteryQuerySequenceRunFactory), ProtocolAssembly.GetExportedTypes());
        Assert.Contains(typeof(IBatteryQuerySequencePort), ProtocolAssembly.GetExportedTypes());
        Assert.Contains(typeof(IAsyncDisposable), typeof(IBatteryValidationPort).GetInterfaces());
        Assert.Contains(typeof(IAsyncDisposable), typeof(IBatteryQuerySequencePort).GetInterfaces());

        MethodInfo open = Assert.Single(typeof(IBatteryQuerySequenceRunFactory).GetMethods());
        Assert.Equal("OpenAsync", open.Name);
        Assert.Equal(
            typeof(Task<HidSessionResult<IBatteryQuerySequencePort>>),
            open.ReturnType);
        Assert.Equal(typeof(CancellationToken), Assert.Single(open.GetParameters()).ParameterType);

        MethodInfo query = Assert.Single(typeof(IBatteryQuerySequencePort).GetMethods());
        Assert.Equal("QueryAsync", query.Name);
        Assert.Equal(
            typeof(Task<HidSessionResult<BatteryValidationSetting>>),
            query.ReturnType);
        Assert.Equal(typeof(CancellationToken), Assert.Single(query.GetParameters()).ParameterType);
    }

    [Fact]
    public void HidSessionDiagnosticIsClosedAndPreservesTwoValueResultShape()
    {
        Assert.Equal(
            [
                "GetCompletion",
                "GetSubmit",
                "Guard",
                "ProtocolValidation",
                "SetCompletion",
                "SetSubmit",
                "SharedGateAdmission",
            ],
            Enum.GetNames<HidSessionFailurePhase>()
                .OrderBy(name => name, StringComparer.Ordinal));

        Dictionary<string, Type> properties = typeof(HidSessionFailureDiagnostic)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToDictionary(property => property.Name, property => property.PropertyType);
        Assert.Equal(5, properties.Count);
        Assert.Equal(typeof(HidSessionFailurePhase), properties["Phase"]);
        Assert.Equal(typeof(HidSessionFailureKind), properties["Failure"]);
        Assert.Equal(typeof(int?), properties["NativeWin32Error"]);
        Assert.Equal(typeof(int?), properties["TransferredByteCount"]);
        Assert.Equal(
            typeof(HidProtocolValidationFailure?),
            properties["ProtocolValidationFailure"]);
        Assert.Equal(
            [
                "Checksum",
                "CommandClassMismatch",
                "CommandIdMismatch",
                "EnvelopeLength",
                "PayloadLength",
                "PayloadValidator",
                "ReportId",
                "Status",
                "TransactionMismatch",
            ],
            Enum.GetNames<HidProtocolValidationFailure>()
                .OrderBy(name => name, StringComparer.Ordinal));

        var result = new HidSessionResult<int>(1, null);
        var (value, failure) = result;
        Assert.Equal(1, value);
        Assert.Null(failure);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void BatteryValidationFactoryExposesOnlyTypedWindowsCompositionAndProbe()
    {
        Type factoryType = typeof(BatteryValidationFactory);
        Assert.True(factoryType.IsPublic);
        Assert.True(factoryType.IsAbstract);
        Assert.True(factoryType.IsSealed);

        Dictionary<string, MethodInfo> methods = factoryType.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToDictionary(method => method.Name, StringComparer.Ordinal);
        Assert.Equal(12, methods.Count);
        Assert.Equal(typeof(IBatteryValidationRunFactory), methods["CreateWindows"].ReturnType);
        Assert.Equal(
            typeof(IBatteryQuerySequenceRunFactory),
            methods["CreateWindowsQuerySequence"].ReturnType);
        Assert.Equal(
            typeof(ITemporaryFullChargeQueryRunFactory),
            methods["CreateWindowsTemporaryFullChargeQuery"].ReturnType);
        Assert.Equal(
            typeof(IFanRpmQueryRunFactory),
            methods["CreateWindowsFanRpmQuery"].ReturnType);
        Assert.Equal(
            typeof(ILightingBrightnessQueryRunner),
            methods["CreateWindowsLightingBrightnessQuery"].ReturnType);
        Assert.Equal(
            typeof(IPerformanceFanDiscriminatorQueryRunner),
            methods["CreateWindowsPerformanceFanDiscriminatorQuery"].ReturnType);
        Assert.Equal(
            typeof(IPerformanceFanSetterValidationRunner),
            methods["CreateWindowsPerformanceFanSetterValidation"].ReturnType);
        Assert.Equal(
            typeof(IProtectionOffValidationRunFactory),
            methods["CreateWindowsProtectionOffValidation"].ReturnType);
        Assert.Equal(
            typeof(ITemporaryFullChargeValidationRunFactory),
            methods["CreateWindowsTemporaryFullChargeValidation"].ReturnType);
        Assert.Equal(typeof(BatteryValidationProbeResult), methods["ProbeWindows"].ReturnType);
        Assert.Equal(
            typeof(IBatteryQuerySequenceRunFactory),
            methods["CreateWindowsQuerySequenceForService"].ReturnType);
        Assert.Equal(
            typeof(BatteryValidationProbeResult),
            methods["ProbeWindowsForService"].ReturnType);
        Assert.All(methods.Values, method => Assert.Empty(method.GetParameters()));
        Assert.Empty(factoryType.GetMembers(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void BatteryValidationProbeResultExposesOnlyClosedMetadata()
    {
        Dictionary<string, Type> properties = typeof(BatteryValidationProbeResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToDictionary(property => property.Name, property => property.PropertyType);

        Assert.Equal(typeof(BatteryValidationProbeStage), properties["Stage"]);
        Assert.Equal(typeof(BatteryValidationProbeClassification), properties["Classification"]);
        Assert.Equal(typeof(BatteryValidationFirmwareCompatibility), properties["FirmwareCompatibility"]);
        Assert.Equal(typeof(HostIdentityFailureDetail), properties["HostIdentityFailure"]);
        Assert.Equal(typeof(string), properties["ObservedSku"]);
        Assert.Equal(typeof(int?), properties["CandidateOpenWin32Error"]);
        Assert.Equal(typeof(int), properties["CandidateAccessFallbackCount"]);
        Assert.Equal(19, properties.Count);
        Assert.DoesNotContain(properties.Keys, name =>
            name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Handle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Report", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Command", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Transport", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VendorId", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ProductId", StringComparison.OrdinalIgnoreCase)
            || name.Contains("UsageId", StringComparison.OrdinalIgnoreCase));
        Assert.All(properties.Values, type => Assert.True(
            type.IsEnum
            || type == typeof(bool)
            || type == typeof(int)
            || type == typeof(int?)
            || type == typeof(string)
            || type == typeof(HostIdentityFailureDetail)));

        Dictionary<string, Type> failureProperties = typeof(HostIdentityFailureDetail)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToDictionary(property => property.Name, property => property.PropertyType);
        Assert.Equal(7, failureProperties.Count);
        Assert.Equal(typeof(HostIdentityFailureReason), failureProperties["Reason"]);
        Assert.Equal(typeof(string), failureProperties["Source"]);
        Assert.Equal(typeof(string), failureProperties["Field"]);
        Assert.Equal(typeof(string), failureProperties["ExpectedValue"]);
        Assert.Equal(typeof(string), failureProperties["ActualValue"]);
        Assert.Equal(typeof(string), failureProperties["ExceptionType"]);
        Assert.Equal(typeof(int?), failureProperties["HResult"]);
    }

    [Fact]
    public void PublicProtocolSignaturesDoNotLeakRawCommandOrNativeTypes()
    {
        List<string> violations = [];

        foreach (Type type in ProtocolAssembly.GetExportedTypes())
        {
            InspectPublicType(type, violations);
        }

        Assert.Empty(violations.OrderBy(violation => violation, StringComparer.Ordinal));
    }

    [Fact]
    public void PublicProtocolExceptionsRemainPublicAndTyped()
    {
        Assert.True(typeof(CapabilityUnavailableException).IsPublic);
        Assert.True(typeof(RazerProtocolException).IsPublic);
        Assert.True(typeof(UnvalidatedCommandException).IsPublic);
        Assert.False(TypeSurfaceContainsForbiddenType(typeof(CapabilityUnavailableException)));
        Assert.False(TypeSurfaceContainsForbiddenType(typeof(RazerProtocolException)));
        Assert.False(TypeSurfaceContainsForbiddenType(typeof(UnvalidatedCommandException)));
    }

    [Fact]
    public void ValidatorRejectsTypeDerivedFromSafeHandle()
    {
        AssertSurfaceRejected(typeof(SafeHandleDerivedSurface));
    }

    [Fact]
    public void ValidatorRejectsInterfaceInheritingRawGenericSurface()
    {
        AssertSurfaceRejected(typeof(IRawGenericSurface));
    }

    [Fact]
    public void ValidatorRejectsSafeHandleGenericConstraints()
    {
        AssertSurfaceRejected(typeof(SafeHandleConstrainedSurface<>));
        AssertSurfaceRejected(typeof(SafeHandleMethodConstrainedSurface));
    }

    [Fact]
    public void ValidatorRejectsUnmanagedFunctionPointerSignature()
    {
        AssertSurfaceRejected(typeof(FunctionPointerSurface));
    }

    private static void AssertSurfaceRejected(Type type)
    {
        List<string> violations = [];
        InspectPublicType(type, violations);
        Assert.NotEmpty(violations);
    }

    private static void InspectPublicType(Type type, ICollection<string> violations)
    {
        if (TypeSurfaceContainsForbiddenType(type))
        {
            violations.Add($"{type.FullName} has a forbidden type hierarchy or generic constraint.");
        }

        InspectMembers(type, violations);
    }

    private static void InspectMembers(Type type, ICollection<string> violations)
    {
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        foreach (ConstructorInfo constructor in type.GetConstructors(flags))
        {
            InspectParameters(type, constructor, constructor.GetParameters(), violations);
        }

        foreach (MethodInfo method in type.GetMethods(flags))
        {
            if (ForbiddenMemberNames.Contains(method.Name))
            {
                violations.Add($"{type.FullName}.{method.Name} exposes a raw command identifier name.");
            }

            if (SignatureContainsForbiddenType(method.ReturnType))
            {
                violations.Add($"{type.FullName}.{method.Name} returns {method.ReturnType}.");
            }

            foreach (Type genericParameter in method.GetGenericArguments())
            {
                if (SignatureContainsForbiddenType(genericParameter))
                {
                    violations.Add(
                        $"{type.FullName}.{method.Name} has a forbidden generic constraint on {genericParameter}.");
                }
            }

            InspectParameters(type, method, method.GetParameters(), violations);
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (ForbiddenMemberNames.Contains(property.Name))
            {
                violations.Add($"{type.FullName}.{property.Name} exposes a raw command identifier name.");
            }

            if (SignatureContainsForbiddenType(property.PropertyType))
            {
                violations.Add($"{type.FullName}.{property.Name} exposes {property.PropertyType}.");
            }

            InspectParameters(type, property, property.GetIndexParameters(), violations);
        }

        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (ForbiddenMemberNames.Contains(field.Name))
            {
                violations.Add($"{type.FullName}.{field.Name} exposes a raw command identifier name.");
            }

            if (SignatureContainsForbiddenType(field.FieldType))
            {
                violations.Add($"{type.FullName}.{field.Name} exposes {field.FieldType}.");
            }
        }

        foreach (EventInfo eventInfo in type.GetEvents(flags))
        {
            if (ForbiddenMemberNames.Contains(eventInfo.Name))
            {
                violations.Add($"{type.FullName}.{eventInfo.Name} exposes a raw command identifier name.");
            }

            if (SignatureContainsForbiddenType(eventInfo.EventHandlerType!))
            {
                violations.Add($"{type.FullName}.{eventInfo.Name} exposes {eventInfo.EventHandlerType}.");
            }
        }
    }

    private static void InspectParameters(
        Type declaringType,
        MemberInfo member,
        IEnumerable<ParameterInfo> parameters,
        ICollection<string> violations)
    {
        foreach (ParameterInfo parameter in parameters)
        {
            if (ForbiddenMemberNames.Contains(parameter.Name ?? string.Empty))
            {
                violations.Add(
                    $"{declaringType.FullName}.{member.Name} exposes parameter '{parameter.Name}'.");
            }

            if (SignatureContainsForbiddenType(parameter.ParameterType))
            {
                violations.Add(
                    $"{declaringType.FullName}.{member.Name} exposes parameter type {parameter.ParameterType}.");
            }
        }
    }

    private static bool SignatureContainsForbiddenType(Type type)
    {
        return SignatureContainsForbiddenType(type, new HashSet<Type>());
    }

    private static bool SignatureContainsForbiddenType(Type type, ISet<Type> visited)
    {
        if (type.IsFunctionPointer)
        {
            _ = SignatureContainsForbiddenType(type.GetFunctionPointerReturnType(), visited);
            foreach (Type parameterType in type.GetFunctionPointerParameterTypes())
            {
                _ = SignatureContainsForbiddenType(parameterType, visited);
            }

            return true;
        }

        if (type.IsPointer)
        {
            _ = SignatureContainsForbiddenType(type.GetElementType()!, visited);
            return true;
        }

        if (type.IsArray || type.IsByRef)
        {
            if (!visited.Add(type))
            {
                return false;
            }

            Type elementType = type.GetElementType()!;
            return elementType == typeof(byte)
                || SignatureContainsForbiddenType(elementType, visited);
        }

        if (type == typeof(IntPtr)
            || type == typeof(UIntPtr)
            || type == typeof(Memory<byte>)
            || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(Span<byte>)
            || type == typeof(ReadOnlySpan<byte>)
            || typeof(SafeHandle).IsAssignableFrom(type)
            || typeof(SafeFileHandle).IsAssignableFrom(type)
            || type == typeof(ThreadPoolBoundHandle)
            || (type.Assembly == ProtocolAssembly && !type.IsVisible)
            || ForbiddenExportedTypeNames.Contains(type.Name))
        {
            return true;
        }

        if (!visited.Add(type))
        {
            return false;
        }

        if (type.IsGenericParameter)
        {
            return type
                .GetGenericParameterConstraints()
                .Any(constraint => SignatureContainsForbiddenType(constraint, visited));
        }

        return type.IsGenericType
            && type
                .GetGenericArguments()
                .Any(argument => SignatureContainsForbiddenType(argument, visited));
    }

    private static bool TypeSurfaceContainsForbiddenType(Type type)
    {
        return TypeSurfaceContainsForbiddenType(type, new HashSet<Type>());
    }

    private static bool TypeSurfaceContainsForbiddenType(Type type, ISet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return false;
        }

        if (SignatureContainsForbiddenType(type))
        {
            return true;
        }

        if (type.BaseType is not null
            && TypeSurfaceContainsForbiddenType(type.BaseType, visited))
        {
            return true;
        }

        return type
            .GetInterfaces()
            .Any(interfaceType => TypeSurfaceContainsForbiddenType(interfaceType, visited));
    }

    private sealed class SafeHandleDerivedSurface : SafeHandle
    {
        public SafeHandleDerivedSurface()
            : base(IntPtr.Zero, ownsHandle: false)
        {
        }

        public override bool IsInvalid => true;

        protected override bool ReleaseHandle() => true;
    }

    private interface IRawGenericSurface : IEnumerable<byte[]>;

    private sealed class SafeHandleConstrainedSurface<T>
        where T : SafeHandle;

    private sealed class SafeHandleMethodConstrainedSurface
    {
        public void Use<T>()
            where T : SafeHandle
        {
        }
    }

    private unsafe sealed class FunctionPointerSurface
    {
        public delegate* unmanaged<int, void> Callback => (delegate* unmanaged<int, void>)0;
    }
}
