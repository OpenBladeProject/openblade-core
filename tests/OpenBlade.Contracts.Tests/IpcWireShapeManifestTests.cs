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
        Assert.Contains("property supportsBreathingColorSelection:System.Boolean member", manifest, StringComparison.Ordinal);
        Assert.Contains("property supportsWaveDirectionSelection:System.Boolean member", manifest, StringComparison.Ordinal);
        Assert.Contains("property blade18KeyboardLayoutId:System.Byte? member", manifest, StringComparison.Ordinal);
        string? expectedHash = OpenBladeIpc.ProtocolVersion switch
        {
            47 => "182C004C3E7FBAFF946EAE2C1CFD6AE86D219A094E9DBC3BEF5A0F800D41F3D1",
            48 => "58A76CD37F81343893C4BF353FEFB02DA5024F1484F98E5D1A1B0EE9F5BC67DF",
            49 => "AEB6AE376C2B39226D3691F3BF2A1E6A3556A45D3351B4E1D4F6B268B51DE087",
            50 => "601FA69AF976662250CCB9D4A9106E4CFE19707314F30A9C9C3540ED818A8F08",
            51 => "FFF27DD55DC614DBFD87C8E34C24F3B942B8F703E8A08D1DF347388EF974014F",
            52 => "678EBF2E077E92A82B7EF989CC32CA82D9B470925C0A7F67FD3F540A9F9E101E",
            53 => "945880E821FDFE1E21EC91E6976458D2EF491186482AD5E5E89954AE3726BBEC",
            54 => "DB989B48B5D11963D3920F4FDB3596903BAE129E5E80E43816D44428D90D7F07",
            55 => "E4B7D20F94C5CA606D23FAAAF3D1067B6AEB0B1654DE35D803DC4A03A264D508",
            56 => "20B1A7546D7244A46C8C84231C733A5A4B011FB63C1A5BD4138924C647B2551B",
            57 => "83418931918B102325CA71E2CB7A531B8C962EC4D09C624C11452590E2878EA9",
            58 => "0C102CB46ED625A92793ADD69ACBF5729AA7A2F77CE12A368FABF0754128B96A",
            59 => "DD8A55A0FE893A4F467D6DF4309B6F418C7AC83CE6667DC450394135A6905532",
            60 => "22C6B28A38E5DE7F74B1F60C2F9916422F5A63B5CF5CFB1FCC96827A2678A860",
            61 => "C3F0F4511480412F4824E3ED87C513B4AA75AC4E4D2451A2032E7398B96AE107",
            62 => "3B33107AAA562645CFC79B87AFDF660BF99A3D63532443783CAFC9693ABC11D7",
            63 => "B72B084B5713FF350E1180DF6B229276BDC86B14AE2ECB9A70EEBCD327146E78",
            64 => "F776B07EA356740DADEDD24F9107C228CAD7F3FFCE74D473312BBC07F9E8BA9D",
            65 => "9419E7F0E4F65585253EAD861401497BA5EFB6CAFFF7A7157F8EE3C46E98FAEF",
            66 => "F66EE64CBD3ACD3625F27D02A1A2E8FF3AA4EEB924EB7DB14D07A79539C626DF",
            67 => "1EAED39617D6E797FD3A09E5D157916D5B3D39039D6080FC0C03C9C4A311F6F4",
            68 => "79120BCCE7C872000361FFFC9926D94DAFE9E7255522F067A2BFCE849DAD37BA",
            69 => "678062B10DF54DF104AA03EE140ECD257F0FB0E803109EFF18DB8032A53B6444",
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
