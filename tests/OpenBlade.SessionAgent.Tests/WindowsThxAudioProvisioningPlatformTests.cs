namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsThxAudioProvisioningPlatformTests
{
    [Fact]
    public void PlanIdentityChangesWithTargetStackConfigurationAndFamily()
    {
        ThxAudioCompatibilityProfile sonya12 = ThxAudioCompatibility.Profiles.Single(
            profile => profile.ModelNumber == "RZ09-0581");
        ThxAudioCompatibilityProfile target = sonya12 with
        {
            Stack = ThxAudioCompatibility.GetProvisioningStack(sonya12),
        };
        string baseline = WindowsThxAudioProvisioningPlatform.GetPlanIdentity(target);

        ThxAudioDriverPackage firstPackage = target.Stack.Packages[0];
        ThxAudioPinnedFile firstFile = firstPackage.Files[0];
        ThxAudioDriverStack changedStack = target.Stack with
        {
            Packages =
            [
                firstPackage with
                {
                    Files = [firstFile with { Sha256 = new string('A', 64) }, .. firstPackage.Files.Skip(1)],
                },
                .. target.Stack.Packages.Skip(1),
            ],
        };
        string changedTargetPin = WindowsThxAudioProvisioningPlatform.GetPlanIdentity(
            target with { Stack = changedStack });
        string changedConfiguration = WindowsThxAudioProvisioningPlatform.GetPlanIdentity(
            target with { ActivePropertiesSha256 = new string('B', 64) });

        ThxAudioCompatibilityProfile rznb11 = ThxAudioCompatibility.Profiles.Single(
            profile => profile.ModelNumber == "RZ09-0528");
        string changedFamily = WindowsThxAudioProvisioningPlatform.GetPlanIdentity(target with
        {
            ProductId = rznb11.ProductId,
            Stack = rznb11.Stack,
        });

        Assert.Equal(baseline, WindowsThxAudioProvisioningPlatform.GetPlanIdentity(target));
        Assert.NotEqual(baseline, changedTargetPin);
        Assert.NotEqual(baseline, changedConfiguration);
        Assert.NotEqual(baseline, changedFamily);
    }
}
