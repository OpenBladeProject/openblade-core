using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class IpcWireShapeManifestTests
{
    [Fact]
    public void RequestAndServerFrameShape_MatchesProtocolKeyedGoldenHash()
    {
        string manifest = CreateManifest(typeof(ServiceRequest), typeof(ServiceServerFrame));
        string actualHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        Assert.Contains(
            "derived getStatus=OpenBlade.Contracts.GetStatusRequest",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "property vbiosGpuName:System.String default=\"Unavailable\"",
            manifest,
            StringComparison.Ordinal);
        string? expectedHash = OpenBladeIpc.ProtocolVersion switch
        {
            47 => "182C004C3E7FBAFF946EAE2C1CFD6AE86D219A094E9DBC3BEF5A0F800D41F3D1",
            48 => "58A76CD37F81343893C4BF353FEFB02DA5024F1484F98E5D1A1B0EE9F5BC67DF",
            _ => null,
        };

        Assert.True(
            expectedHash is not null,
            $"IPC protocol {OpenBladeIpc.ProtocolVersion} has no reviewed wire-shape golden. " +
            $"Review the manifest and add its hash: {actualHash}\n{manifest}");
        Assert.True(
            string.Equals(expectedHash, actualHash, StringComparison.Ordinal),
            $"IPC wire shape changed without a protocol-keyed golden update. " +
            $"If the change is intentional, bump the protocol, review this manifest, and " +
            $"record hash {actualHash}.\n{manifest}");
    }

    private static string CreateManifest(params Type[] roots)
    {
        var discovered = new HashSet<Type>();
        foreach (Type root in roots)
        {
            DiscoverContractTypes(root, discovered);
        }

        var lines = new List<string>();
        foreach (Type type in discovered.OrderBy(FormatType, StringComparer.Ordinal))
        {
            lines.Add($"type {FormatType(type)}");
            if (type.IsEnum)
            {
                foreach (string name in Enum.GetNames(type).Order(StringComparer.Ordinal))
                {
                    object value = Enum.Parse(type, name);
                    lines.Add(
                        $"  enum {name}={Convert.ToInt64(value, CultureInfo.InvariantCulture)}");
                }
            }

            JsonPolymorphicAttribute? polymorphic =
                type.GetCustomAttribute<JsonPolymorphicAttribute>();
            if (polymorphic is not null)
            {
                lines.Add(
                    $"  discriminator {polymorphic.TypeDiscriminatorPropertyName ?? "$type"}");
                foreach (JsonDerivedTypeAttribute derived in type
                    .GetCustomAttributes<JsonDerivedTypeAttribute>()
                    .OrderBy(
                        attribute => attribute.TypeDiscriminator?.ToString(),
                        StringComparer.Ordinal))
                {
                    lines.Add(
                        $"  derived {derived.TypeDiscriminator}={FormatType(derived.DerivedType)}");
                }
            }

            ConstructorInfo? constructor = GetSerializationConstructor(type);
            foreach (PropertyInfo property in GetSerializableProperties(type)
                .OrderBy(GetJsonPropertyName, StringComparer.Ordinal))
            {
                string requirement = GetRequirement(property, constructor);
                JsonIgnoreAttribute? ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
                string ignoreCondition = ignore is null
                    ? string.Empty
                    : $" ignore={ignore.Condition}";
                lines.Add(
                    $"  property {GetJsonPropertyName(property)}:{FormatType(property.PropertyType)} " +
                    $"{requirement}{ignoreCondition}");
            }
        }

        return string.Join('\n', lines) + '\n';
    }

    private static void DiscoverContractTypes(Type type, ISet<Type> discovered)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            DiscoverContractTypes(type.GetElementType()!, discovered);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                DiscoverContractTypes(argument, discovered);
            }
        }

        if (!string.Equals(
                type.Namespace,
                typeof(ServiceRequest).Namespace,
                StringComparison.Ordinal)
            || !discovered.Add(type))
        {
            return;
        }

        foreach (JsonDerivedTypeAttribute derived in
            type.GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            DiscoverContractTypes(derived.DerivedType, discovered);
        }

        foreach (PropertyInfo property in GetSerializableProperties(type))
        {
            DiscoverContractTypes(property.PropertyType, discovered);
        }
    }

    private static IEnumerable<PropertyInfo> GetSerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.GetMethod?.IsPublic == true
                && property.GetIndexParameters().Length == 0
                && property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition
                    != JsonIgnoreCondition.Always);

    private static ConstructorInfo? GetSerializationConstructor(Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        ConstructorInfo[] attributed = constructors
            .Where(constructor => constructor.GetCustomAttribute<JsonConstructorAttribute>() is not null)
            .ToArray();
        if (attributed.Length == 1)
        {
            return attributed[0];
        }

        ConstructorInfo? parameterless = constructors.SingleOrDefault(
            constructor => constructor.GetParameters().Length == 0);
        return parameterless ?? (constructors.Length == 1 ? constructors[0] : null);
    }

    private static string GetRequirement(
        PropertyInfo property,
        ConstructorInfo? constructor)
    {
        if (property.GetCustomAttribute<JsonRequiredAttribute>() is not null)
        {
            return "required";
        }

        ParameterInfo? parameter = constructor?.GetParameters().SingleOrDefault(candidate =>
            string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));
        if (parameter is null)
        {
            return "member";
        }

        return parameter.HasDefaultValue
            ? $"default={FormatDefault(parameter.DefaultValue)}"
            : "constructor-required";
    }

    private static string GetJsonPropertyName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static string FormatDefault(object? value) => value switch
    {
        null => "null",
        string text => JsonSerializer.Serialize(text),
        bool boolean => boolean ? "true" : "false",
        Enum enumeration => Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatType(Type type)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            return $"{FormatType(nullable)}?";
        }

        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string name = type.GetGenericTypeDefinition().FullName
            ?? type.GetGenericTypeDefinition().Name;
        int arityMarker = name.IndexOf('`', StringComparison.Ordinal);
        if (arityMarker >= 0)
        {
            name = name[..arityMarker];
        }

        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(FormatType))}>";
    }
}
