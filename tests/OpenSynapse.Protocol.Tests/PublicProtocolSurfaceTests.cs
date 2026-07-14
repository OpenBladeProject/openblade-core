using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

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
        Assert.Contains(typeof(IAsyncDisposable), typeof(IBatteryValidationPort).GetInterfaces());
    }

    [Fact]
    public void PublicProtocolSignaturesDoNotLeakRawCommandOrNativeTypes()
    {
        List<string> violations = [];

        foreach (Type type in ProtocolAssembly.GetExportedTypes())
        {
            InspectMembers(type, violations);
        }

        Assert.Empty(violations.OrderBy(violation => violation, StringComparer.Ordinal));
    }

    [Fact]
    public void PublicProtocolExceptionsRemainPublicAndTyped()
    {
        Assert.True(typeof(RazerProtocolException).IsPublic);
        Assert.True(typeof(UnvalidatedCommandException).IsPublic);
        Assert.False(SignatureContainsForbiddenType(typeof(RazerProtocolException)));
        Assert.False(SignatureContainsForbiddenType(typeof(UnvalidatedCommandException)));
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
        if (type.IsPointer)
        {
            return true;
        }

        if (type.IsArray || type.IsByRef)
        {
            Type elementType = type.GetElementType()!;
            return elementType == typeof(byte) || SignatureContainsForbiddenType(elementType);
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

        return type.IsGenericType
            && type.GetGenericArguments().Any(SignatureContainsForbiddenType);
    }
}
