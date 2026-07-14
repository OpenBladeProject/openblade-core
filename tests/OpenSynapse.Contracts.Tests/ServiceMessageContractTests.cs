using System.Reflection;
using System.Text.Json.Serialization;
using OpenSynapse.Contracts;

namespace OpenSynapse.Contracts.Tests;

public sealed class ServiceMessageContractTests
{
    [Fact]
    public void ProtocolConstants_AreClosedVersionTwoAndRetainExactSizeLimit()
    {
        Assert.Equal(2, OpenSynapseIpc.ProtocolVersion);
        Assert.Equal("OpenSynapse.Service.v2", OpenSynapseIpc.PipeName);
        Assert.Equal(64 * 1024, OpenSynapseIpc.MaximumMessageBytes);
    }

    [Fact]
    public void RequestAndServerFrameBases_DeclareOnlyTheClosedDerivedTypes()
    {
        AssertDerivedTypes(
            typeof(ServiceRequest),
            (typeof(GetStatusRequest), "getStatus"),
            (typeof(SubscribeStatusRequest), "subscribeStatus"),
            (typeof(UpdateProfilesRequest), "updateProfiles"),
            (typeof(SetPerformanceOverrideRequest), "setPerformanceOverride"),
            (typeof(SetFanOverrideRequest), "setFanOverride"),
            (typeof(SetLightingOverrideRequest), "setLightingOverride"),
            (typeof(SetBatteryPolicyRequest), "setBatteryPolicy"),
            (typeof(SetFullChargeOverrideRequest), "setFullChargeOverride"));
        AssertDerivedTypes(
            typeof(ServiceServerFrame),
            (typeof(ServiceResponse), "response"),
            (typeof(ServiceStatusEvent), "statusEvent"));
    }

    [Fact]
    public void ServiceStatusSnapshot_ContainsFullControlPlaneState()
    {
        string[] expected =
        [
            "Revision", "DeviceVerified", "WritesBlocked", "Conflicts", "Message",
            "Capabilities", "ConfirmedState", "Profiles", "DesiredState", "Telemetry",
            "PerformanceOverride", "FanOverride", "LightingOverride", "FullChargeOverride",
        ];

        Assert.Equal(
            expected.Order(),
            typeof(ServiceStatusSnapshot).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(typeof(DeviceCapabilities), typeof(ServiceStatusSnapshot).GetProperty("Capabilities")!.PropertyType);
        Assert.Equal(typeof(ConfirmedDeviceState), typeof(ServiceStatusSnapshot).GetProperty("ConfirmedState")!.PropertyType);
    }

    [Fact]
    public void ServiceResponse_ExposesStatusOrControlResultAndStatusEventHasNoRequestId()
    {
        Assert.Equal(
            new[] { "Error", "RequestId", "Result", "Status", "Success", "Version" },
            typeof(ServiceResponse).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain(
            typeof(ServiceStatusEvent).GetProperties(),
            property => property.Name == "RequestId");
        Assert.Equal(
            typeof(ControlOperationResult),
            typeof(ServiceResponse).GetProperty("Result")!.PropertyType);
    }

    [Fact]
    public void UnavailableCapabilities_ContainNoHardwareClaims()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();

        Assert.Empty(capabilities.PerformancePresets);
        Assert.Empty(capabilities.FanModes);
        Assert.Empty(capabilities.LightingEffects);
        Assert.Equal(0, capabilities.MinimumChargeLimit);
        Assert.Equal(0, capabilities.MaximumChargeLimit);
        Assert.Equal(0, capabilities.ChargeLimitStep);
    }

    [Fact]
    public void ExportedMessageSurface_ContainsNoRawOrFirmwareCommandFields()
    {
        Type[] roots = [typeof(ServiceRequest), typeof(ServiceServerFrame)];
        HashSet<Type> visited = [];

        foreach (Type root in roots)
        {
            AssertSafeSurface(root, visited);
        }
    }

    private static void AssertDerivedTypes(
        Type baseType,
        params (Type Type, string Discriminator)[] expected)
    {
        JsonPolymorphicAttribute polymorphic = Assert.Single(
            baseType.GetCustomAttributes<JsonPolymorphicAttribute>());
        Assert.NotNull(polymorphic.TypeDiscriminatorPropertyName);
        JsonDerivedTypeAttribute[] actual =
            baseType.GetCustomAttributes<JsonDerivedTypeAttribute>().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        foreach ((Type type, string discriminator) in expected)
        {
            Assert.Contains(actual, attribute =>
                attribute.DerivedType == type
                && string.Equals(attribute.TypeDiscriminator?.ToString(), discriminator, StringComparison.Ordinal));
        }
    }

    private static void AssertSafeSurface(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            Assert.NotEqual(typeof(byte), type.GetElementType());
            AssertSafeSurface(type.GetElementType()!, visited);
            return;
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            Assert.NotEqual(typeof(Memory<>), definition);
            Assert.NotEqual(typeof(ReadOnlyMemory<>), definition);
            foreach (Type argument in type.GetGenericArguments())
            {
                AssertSafeSurface(argument, visited);
            }
        }

        if (!visited.Add(type) || type.Namespace != typeof(ServiceRequest).Namespace)
        {
            return;
        }

        Assert.NotEqual(typeof(IntPtr), type);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.DoesNotContain(
                property.Name,
                ["transactionId", "commandClass", "commandId"],
                StringComparer.OrdinalIgnoreCase);
            AssertSafeSurface(property.PropertyType, visited);
        }

        foreach (JsonDerivedTypeAttribute derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            AssertSafeSurface(derived.DerivedType, visited);
        }
    }
}
