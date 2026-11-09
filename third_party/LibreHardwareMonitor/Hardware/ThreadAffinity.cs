// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

// OpenBlade modification, 2026-11-07: Windows-only thermal source closure.
using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;


namespace LibreHardwareMonitor.Hardware;

internal static class ThreadAffinity
{
    /// <summary>
    /// Initializes static members of the <see cref="ThreadAffinity" /> class.
    /// </summary>
    static ThreadAffinity()
    {
        ProcessorGroupCount = PInvoke.GetActiveProcessorGroupCount();

        if (ProcessorGroupCount < 1)
            ProcessorGroupCount = 1;
    }

    /// <summary>
    /// Gets the processor group count.
    /// </summary>
    public static int ProcessorGroupCount { get; }

    /// <summary>
    /// Sets the processor group affinity for the current thread.
    /// </summary>
    /// <param name="affinity">The processor group affinity.</param>
    /// <returns>The previous processor group affinity.</returns>
    public static unsafe GroupAffinity Set(GroupAffinity affinity)
    {
        if (affinity == GroupAffinity.Undefined)
            return GroupAffinity.Undefined;

        ulong maxValue = IntPtr.Size == 8 ? ulong.MaxValue : uint.MaxValue;
        if (affinity.Mask > maxValue)
            throw new ArgumentOutOfRangeException(nameof(affinity));

        var groupAffinity = new GROUP_AFFINITY { Group = affinity.Group, Mask = (UIntPtr)affinity.Mask };
        GROUP_AFFINITY previousGroupAffinity = new();

        IntPtr currentThread = PInvoke.GetCurrentThread();

        return PInvoke.SetThreadGroupAffinity(new HANDLE(currentThread),
                                              &groupAffinity,
                                              &previousGroupAffinity)
            ? new GroupAffinity(previousGroupAffinity.Group, previousGroupAffinity.Mask)
            : GroupAffinity.Undefined;
    }
}

