// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// OpenBlade addition, 2026-11-07: isolated CPU-temperature-only facade.
using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Cpu;

namespace LibreHardwareMonitor;

/// <summary>
/// Discovers only reviewed CPU thermal sensors. It never initializes motherboard,
/// GPU, USB, power, tuning, or RyzenSMU telemetry. Use one serialized owner per process.
/// </summary>
public sealed class CpuTemperatureComputer : IDisposable
{
    private CpuGroup _group;
    private bool _opened;

    public IReadOnlyList<IHardware> Hardware => _group?.Hardware ?? Array.Empty<IHardware>();

    public void Open()
    {
        if (_opened)
            return;
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("CPU temperature sampling requires a 64-bit Windows process.");
        try
        {
            OpCode.Open();
            Mutexes.Open();
            _group = new CpuGroup(new EphemeralSettings());
            _opened = true;
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Update()
    {
        if (!_opened)
            throw new InvalidOperationException("The CPU temperature provider has not been opened.");
        UpdateHardware(Hardware);
    }

    internal static void UpdateHardware(IReadOnlyList<IHardware> hardwareList)
    {
        ClearValues(hardwareList);
        try
        {
            foreach (IHardware hardware in hardwareList)
                hardware.Update();
            foreach (IHardware hardware in hardwareList)
                foreach (ISensor sensor in hardware.Sensors)
                    sensor.ValuesTimeWindow = TimeSpan.Zero;
        }
        catch
        {
            // A later failure invalidates values assigned earlier in this same update.
            ClearValues(hardwareList);
            throw;
        }
    }

    private static void ClearValues(IReadOnlyList<IHardware> hardwareList)
    {
        foreach (IHardware hardware in hardwareList)
            foreach (ISensor sensor in hardware.Sensors)
            {
                sensor.ValuesTimeWindow = TimeSpan.Zero;
                if (sensor is not Sensor concrete)
                    throw new InvalidOperationException("The CPU sensor has no reviewed freshness reset contract.");
                concrete.Value = null;
            }
    }

    public void Close()
    {
        try { _group?.Close(); }
        finally
        {
            _group = null;
            _opened = false;
            Mutexes.Close();
            OpCode.Close();
        }
    }

    public void Dispose() => Close();

    private sealed class EphemeralSettings : ISettings
    {
        public bool Contains(string name) => false;
        public void SetValue(string name, string value) { }
        public string GetValue(string name, string value) => value;
        public void Remove(string name) { }
    }
}
