using System.Globalization;
using System.Reflection;

namespace OpenBlade.Testing;

internal static class PublicApiSurface
{
    private const string BaselineHeader =
        "# Reviewed exported API baseline. Regenerate with build/Update-PublicApiBaselines.ps1 and review the diff.";

    public static string[] Capture(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly
            .GetExportedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(CaptureType)
            .ToArray();
    }

    public static string[] ReadBaseline(string path) =>
        File.ReadAllLines(path)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

    public static bool UpdateBaselineIfRequested(
        string repositoryRelativePath,
        IReadOnlyCollection<string> surface)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("OPENBLADE_UPDATE_PUBLIC_API_BASELINES"),
            "1",
            StringComparison.Ordinal))
        {
            return false;
        }

        string repositoryRoot =
            Environment.GetEnvironmentVariable("OPENBLADE_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException(
                "OPENBLADE_REPOSITORY_ROOT is required when updating API baselines.");
        string path = Path.Combine(
            repositoryRoot,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllLines(path, [BaselineHeader, .. surface]);
        return true;
    }

    private static IEnumerable<string> CaptureType(Type type)
    {
        yield return $"type {FormatTypeDeclaration(type)}";

        if (type.BaseType is { } baseType
            && baseType != typeof(object)
            && baseType != typeof(ValueType)
            && baseType != typeof(Enum)
            && baseType != typeof(MulticastDelegate))
        {
            yield return $"  base {FormatType(baseType)}";
        }

        IEnumerable<Type> inheritedInterfaces = type.BaseType?.GetInterfaces() ?? [];
        foreach (Type interfaceType in type
            .GetInterfaces()
            .Except(inheritedInterfaces)
            .OrderBy(FormatType, StringComparer.Ordinal))
        {
            yield return $"  interface {FormatType(interfaceType)}";
        }

        foreach (string constraint in FormatGenericConstraints(type.GetGenericArguments()))
        {
            yield return $"  {constraint}";
        }

        const BindingFlags declaredPublic = BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        string[] members =
        [
            .. type.GetConstructors(declaredPublic).Select(FormatConstructor),
            .. type.GetMethods(declaredPublic).Select(FormatMethod),
            .. type.GetProperties(declaredPublic).Select(FormatProperty),
            .. type.GetFields(declaredPublic).Select(FormatField),
            .. type.GetEvents(declaredPublic).Select(FormatEvent),
        ];
        foreach (string member in members
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            yield return $"  {member}";
        }
    }

    private static string FormatTypeDeclaration(Type type)
    {
        string kind = type.IsEnum
            ? "enum"
            : typeof(MulticastDelegate).IsAssignableFrom(type.BaseType)
                ? "delegate"
                : type.IsInterface
                    ? "interface"
                    : type.IsValueType
                        ? "struct"
                        : "class";
        string modifiers = type.IsAbstract && type.IsSealed
            ? "static "
            : type.IsAbstract && !type.IsInterface
                ? "abstract "
                : type.IsSealed && !type.IsValueType && kind != "delegate"
                    ? "sealed "
                    : string.Empty;
        return $"{modifiers}{kind} {FormatType(type)}";
    }

    private static string FormatConstructor(ConstructorInfo constructor) =>
        $"ctor {FormatParameters(constructor.GetParameters())}";

    private static string FormatMethod(MethodInfo method)
    {
        string modifiers = method.IsStatic
            ? "static "
            : method.IsAbstract
                ? "abstract "
                : method.GetBaseDefinition() != method
                    ? method.IsFinal
                        ? "sealed override "
                        : "override "
                    : method.IsVirtual
                        ? "virtual "
                        : string.Empty;
        string genericParameters = method.IsGenericMethodDefinition
            ? $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>"
            : string.Empty;
        string signature =
            $"method {modifiers}{FormatType(method.ReturnType)} {method.Name}{genericParameters}" +
            $"{FormatParameters(method.GetParameters())}";
        string[] constraints = FormatGenericConstraints(method.GetGenericArguments()).ToArray();
        return constraints.Length == 0
            ? signature
            : $"{signature} {string.Join(" ", constraints)}";
    }

    private static string FormatProperty(PropertyInfo property)
    {
        string index = property.GetIndexParameters().Length == 0
            ? string.Empty
            : FormatParameters(property.GetIndexParameters());
        List<string> accessors = [];
        if (property.GetMethod?.IsPublic == true)
        {
            accessors.Add("get");
        }
        if (property.SetMethod?.IsPublic == true)
        {
            bool initOnly = property.SetMethod.ReturnParameter
                .GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));
            accessors.Add(initOnly ? "init" : "set");
        }

        return $"property {FormatType(property.PropertyType)} {property.Name}{index} " +
            $"{{ {string.Join("; ", accessors)}; }}";
    }

    private static string FormatField(FieldInfo field)
    {
        string modifiers = field.IsLiteral
            ? "const "
            : field.IsStatic && field.IsInitOnly
                ? "static readonly "
                : field.IsStatic
                    ? "static "
                    : field.IsInitOnly
                        ? "readonly "
                        : string.Empty;
        string value = field.IsLiteral
            ? $" = {FormatConstant(field.GetRawConstantValue())}"
            : string.Empty;
        return $"field {modifiers}{FormatType(field.FieldType)} {field.Name}{value}";
    }

    private static string FormatEvent(EventInfo eventInfo) =>
        $"event {FormatType(eventInfo.EventHandlerType!)} {eventInfo.Name}";

    private static string FormatParameters(IEnumerable<ParameterInfo> parameters) =>
        $"({string.Join(", ", parameters.Select(FormatParameter))})";

    private static string FormatParameter(ParameterInfo parameter)
    {
        Type parameterType = parameter.ParameterType;
        string modifier = string.Empty;
        if (parameterType.IsByRef)
        {
            modifier = parameter.IsOut
                ? "out "
                : parameter.IsIn
                    ? "in "
                    : "ref ";
            parameterType = parameterType.GetElementType()!;
        }

        string defaultValue = parameter.HasDefaultValue
            ? $" = {FormatConstant(parameter.DefaultValue)}"
            : string.Empty;
        return $"{modifier}{FormatType(parameterType)} {parameter.Name}{defaultValue}";
    }

    private static IEnumerable<string> FormatGenericConstraints(
        IEnumerable<Type> genericArguments)
    {
        foreach (Type argument in genericArguments.Where(type => type.IsGenericParameter))
        {
            var constraints = new List<string>();
            GenericParameterAttributes attributes =
                argument.GenericParameterAttributes
                & GenericParameterAttributes.SpecialConstraintMask;
            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                constraints.Add("class");
            }
            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                constraints.Add("struct");
            }
            constraints.AddRange(argument
                .GetGenericParameterConstraints()
                .Where(constraint => constraint != typeof(ValueType))
                .Select(FormatType));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
                && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                yield return $"where {argument.Name} : {string.Join(", ", constraints)}";
            }
        }
    }

    private static string FormatType(Type type)
    {
        if (type.IsArray)
        {
            string commas = new(',', type.GetArrayRank() - 1);
            return $"{FormatType(type.GetElementType()!)}[{commas}]";
        }
        if (type.IsPointer)
        {
            return $"{FormatType(type.GetElementType()!)}*";
        }
        if (type.IsByRef)
        {
            return $"{FormatType(type.GetElementType()!)}&";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (!type.IsGenericType)
        {
            return (type.FullName ?? type.Name).Replace('+', '.');
        }

        string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }
        return $"{name.Replace('+', '.')}<" +
            $"{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
    }

    private static string FormatConstant(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        char character => $"'{character}'",
        bool boolean => boolean ? "true" : "false",
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
