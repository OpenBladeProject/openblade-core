namespace OpenSynapse.Protocol;

public enum BatteryValidationSetting
{
    ProtectionOff,
    Limit50,
    Limit55,
    Limit60,
    Limit65,
    Limit70,
    Limit75,
    Limit80,
}

public static class BatteryValidationProtocol
{
    private const byte CommandClass = 0x07;
    private const byte QueryCommandId = 0x92;
    private const byte SetCommandId = 0x12;

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, CommandClass, QueryCommandId, [0x00]);

    public static RazerReport CreateSet(
        byte transactionId,
        BatteryValidationSetting setting) =>
        RazerReport.Create(
            transactionId,
            CommandClass,
            SetCommandId,
            [Encode(setting)]);

    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out BatteryValidationSetting setting)
    {
        setting = default;
        if (payload.Length != 1)
        {
            return false;
        }

        return payload[0] switch
        {
            0x50 => Assign(BatteryValidationSetting.ProtectionOff, out setting),
            0xB2 => Assign(BatteryValidationSetting.Limit50, out setting),
            0xB7 => Assign(BatteryValidationSetting.Limit55, out setting),
            0xBC => Assign(BatteryValidationSetting.Limit60, out setting),
            0xC1 => Assign(BatteryValidationSetting.Limit65, out setting),
            0xC6 => Assign(BatteryValidationSetting.Limit70, out setting),
            0xCB => Assign(BatteryValidationSetting.Limit75, out setting),
            0xD0 => Assign(BatteryValidationSetting.Limit80, out setting),
            _ => false,
        };
    }

    private static byte Encode(BatteryValidationSetting setting) => setting switch
    {
        BatteryValidationSetting.ProtectionOff => 0x50,
        BatteryValidationSetting.Limit50 => 0xB2,
        BatteryValidationSetting.Limit55 => 0xB7,
        BatteryValidationSetting.Limit60 => 0xBC,
        BatteryValidationSetting.Limit65 => 0xC1,
        BatteryValidationSetting.Limit70 => 0xC6,
        BatteryValidationSetting.Limit75 => 0xCB,
        BatteryValidationSetting.Limit80 => 0xD0,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown battery validation setting."),
    };

    private static bool Assign(
        BatteryValidationSetting value,
        out BatteryValidationSetting setting)
    {
        setting = value;
        return true;
    }
}
