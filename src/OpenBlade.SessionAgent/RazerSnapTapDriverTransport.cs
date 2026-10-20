using System.ComponentModel;

namespace OpenBlade.SessionAgent;

/// <summary>The fixed A/D contract of the trusted, model-bound RzCommon keyboard endpoint.</summary>
internal sealed class RazerSnapTapDriverTransport : IRazerSnapTapHookTransport, IDisposable
{
    private readonly RazerSnapTapNativeIo io;
    private readonly Func<bool>? canEnable;
    private readonly Func<bool>? canOutput;

    internal RazerSnapTapDriverTransport(string path, Func<bool> isCurrent,
        Func<bool>? canEnable = null, Func<bool>? canOutput = null)
    {
        this.canEnable = canEnable;
        this.canOutput = canOutput;
        io = new(path, isCurrent);
    }

    public IReadOnlyList<byte[]> ReadHooks()
    {
        var hooks = new List<byte[]>();
        for (uint index = 0; index < RazerSnapTapDriverProtocol.MaximumHookEnumerationCount; index++)
        {
            byte[]? record;
            try
            {
                record = io.Request(RazerSnapTapDriverProtocol.EnumInputHookIoControlCode,
                    RazerSnapTapDriverProtocol.EncodeHookIndex(index),
                    RazerSnapTapDriverProtocol.HookRecordLength, 250, true);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 31)
            {
                // RzCommon's index-out-of-range branch returns STATUS_UNSUCCESSFUL,
                // translated by Windows to ERROR_GEN_FAILURE. Other errors are fatal.
                return hooks;
            }
            if (record is null || !RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out _))
            {
                throw new InvalidDataException("Keyboard hook enumeration did not complete.");
            }
            hooks.Add(record);
        }
        throw new InvalidDataException("Keyboard hook enumeration exceeded its bound.");
    }

    public void SetHook(byte[] record)
    {
        if (!RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var snapshot)
            || snapshot is null || !RazerSnapTapDriverProtocol.IsOwnedAdHook(snapshot))
        {
            throw new InvalidDataException("Only the fixed keyboard hooks are admitted.");
        }
        Mutate(RazerSnapTapDriverProtocol.SetInputHookIoControlCode, record);
    }

    public void ClearHook(byte[] record)
    {
        if (record.Length != 32 || !(record.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeClearHook(0x1E))
            || record.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeClearHook(0x20))))
        {
            throw new InvalidDataException("Only the fixed keyboard hooks are admitted.");
        }
        Mutate(RazerSnapTapDriverProtocol.ClearInputHookIoControlCode, record);
    }

    public void SetHookEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Disabling the aggregate hook flag invokes the driver's broad
            // pressed-key reset. Close this file's subscription instead.
            throw new InvalidOperationException("Close the keyboard subscription instead.");
        }
        io.Request(RazerSnapTapDriverProtocol.EnableInputHookIoControlCode,
            RazerSnapTapDriverProtocol.EncodeHookEnabled(true), 0, 250, false,
            canEnable ?? (() => false));
    }

    public void CloseSubscription() => io.Dispose();

    internal bool TryRead(out SnapTapKeyStroke stroke)
    {
        byte[]? packet = io.Request(RazerSnapTapDriverProtocol.NotificationIoControlCode, [],
            RazerSnapTapDriverProtocol.NotificationLength, 10, true);
        stroke = default;
        if (packet is null)
        {
            return false;
        }
        var decoded = RazerSnapTapDriverProtocol.DecodeInput(packet, packet.Length, out stroke);
        if (decoded == RazerSnapTapInputDecodeResult.Malformed)
        {
            throw new InvalidDataException("Unexpected keyboard driver event.");
        }
        return decoded == RazerSnapTapInputDecodeResult.Decoded;
    }

    internal void Send(SnapTapKeyStroke stroke) =>
        io.Request(RazerSnapTapDriverProtocol.SendInputIoControlCode,
            RazerSnapTapDriverProtocol.EncodeOutput(stroke), 0, 250, false,
            canOutput ?? (() => false));

    private void Mutate(uint code, byte[] packet) => io.Request(code, packet, 0, 250, false);

    public void Dispose() => io.Dispose();
}
