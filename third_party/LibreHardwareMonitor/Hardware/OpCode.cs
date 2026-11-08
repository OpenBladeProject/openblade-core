// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

// OpenBlade modification, 2026-11-07: CPU-temperature-only source subset; see README.OpenBlade.md.

using System;
using System.Runtime.Intrinsics.X86;

namespace LibreHardwareMonitor.Hardware;

internal static class OpCode
{
    public delegate bool CpuidDelegate(uint index, uint ecxValue, out uint eax, out uint ebx, out uint ecx, out uint edx);
    public static CpuidDelegate CpuId = ReadCpuId;
    public static void Open()
    {
        if (!X86Base.IsSupported)
            throw new PlatformNotSupportedException("The CPU temperature provider requires an x86/x64 CPU.");
    }
    public static void Close() { }
    private static bool ReadCpuId(uint index, uint ecxValue, out uint eax, out uint ebx, out uint ecx, out uint edx)
    {
        var value = X86Base.CpuId(unchecked((int)index), unchecked((int)ecxValue));
        eax = unchecked((uint)value.Eax);
        ebx = unchecked((uint)value.Ebx);
        ecx = unchecked((uint)value.Ecx);
        edx = unchecked((uint)value.Edx);
        return true;
    }
}
