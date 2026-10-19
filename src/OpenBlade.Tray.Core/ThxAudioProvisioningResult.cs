namespace OpenBlade.Tray;

public enum ThxAudioProvisioningResult
{
    Installed = 0,
    Failed = 1,
    Unavailable = 2,
    AlreadyInstalled = 3,
    RecoveryRequired = 4,
    SourceUnavailable = 5,
    ExistingInstallation = 6,
    AcPowerRequired = 10,
    ControllerActive = 11,
    BaseAudioUnverified = 12,
    Canceled = 1602,
    RestartRequired = 3010,
}
