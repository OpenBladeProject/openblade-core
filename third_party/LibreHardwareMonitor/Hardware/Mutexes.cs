// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// OpenBlade modification, 2026-11-07: retain only the shared indexed-read PCI mutex;
// failure to establish the mutex must not silently remove serialization.
using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LibreHardwareMonitor.Hardware;

internal static class Mutexes
{
    private static Mutex _pciBusMutex;
    internal static void Open()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        try { _pciBusMutex = MutexAcl.Create(false, "Global\\Access_PCI", out _, security); }
        catch (UnauthorizedAccessException) { _pciBusMutex = Mutex.OpenExisting("Global\\Access_PCI"); }
    }
    internal static bool WaitPciBus(int millisecondsTimeout)
    {
        if (_pciBusMutex == null)
            throw new InvalidOperationException("The PCI read mutex is not open.");
        try { return _pciBusMutex.WaitOne(millisecondsTimeout, false); }
        catch (AbandonedMutexException) { return true; }
    }
    internal static void ReleasePciBus() => _pciBusMutex.ReleaseMutex();
    internal static void Close()
    {
        _pciBusMutex?.Dispose();
        _pciBusMutex = null;
    }
}
