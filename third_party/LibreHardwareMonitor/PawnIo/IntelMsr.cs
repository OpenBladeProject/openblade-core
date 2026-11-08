// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// OpenBlade modification, 2026-11-07: restore CPU sampling affinity in finally.
using LibreHardwareMonitor.Hardware;

namespace LibreHardwareMonitor.PawnIo;

internal sealed class IntelMsr
{
    private readonly long[] _inArray = new long[1];
    private readonly PawnIo _pawnIO = PawnIo.LoadModuleFromResource(typeof(IntelMsr).Assembly, $"{nameof(LibreHardwareMonitor)}.Resources.PawnIo.IntelMSR.bin");

    public bool ReadMsr(uint index, out ulong value)
    {
        _inArray[0] = index;
        value = 0;
        try
        {
            long[] outArray = _pawnIO.Execute("ioctl_read_msr", _inArray, 1);
            value = (ulong)outArray[0];
        }
        catch
        {
            return false;
        }

        return true;
    }

    public bool ReadMsr(uint index, out uint eax, out uint edx)
    {
        _inArray[0] = index;
        eax = 0;
        edx = 0;
        try
        {
            long[] outArray = _pawnIO.Execute("ioctl_read_msr", _inArray, 1);
            eax = (uint)outArray[0];
            edx = (uint)(outArray[0] >> 32);
        }
        catch
        {
            return false;
        }

        return true;
    }

    public bool ReadMsr(uint index, out uint eax, out uint edx, GroupAffinity affinity)
    {
        GroupAffinity previousAffinity = ThreadAffinity.Set(affinity);
        if (previousAffinity == GroupAffinity.Undefined)
            throw new System.IO.IOException("The CPU sampling thread affinity could not be established.");
        try { return ReadMsr(index, out eax, out edx); }
        finally
        {
            if (ThreadAffinity.Set(previousAffinity) == GroupAffinity.Undefined)
                throw new System.IO.IOException("The CPU sampling thread affinity could not be restored.");
        }
    }
    public void Close() => _pawnIO.Close();
}

