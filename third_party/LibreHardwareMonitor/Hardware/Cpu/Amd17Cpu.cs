// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// OpenBlade modification, 2026-11-07: retain the primary Amd17Cpu thermal decoder only;
// omit SMU, CCD, core-clock, voltage, and power telemetry and release read ownership in finally.
using System;
using System.IO;
using LibreHardwareMonitor.PawnIo;

namespace LibreHardwareMonitor.Hardware.Cpu;

internal sealed class Amd17Cpu : AmdCpu
{
    private readonly AmdFamily17 _pawnModule;
    private readonly Sensor _coreTemperatureTctl;
    private readonly Sensor _coreTemperatureTdie;
    private readonly Sensor _coreTemperatureTctlTdie;

    public Amd17Cpu(int processorIndex, CpuId[][] cpuId, ISettings settings) : base(processorIndex, cpuId, settings)
    {
        _coreTemperatureTctl = new Sensor("Core (Tctl)", 0, SensorType.Temperature, this, settings);
        _coreTemperatureTdie = new Sensor("Core (Tdie)", 1, SensorType.Temperature, this, settings);
        _coreTemperatureTctlTdie = new Sensor("Core (Tctl/Tdie)", 2, SensorType.Temperature, this, settings);
        _pawnModule = new AmdFamily17();
    }

    public override void Update()
    {
        foreach (ISensor sensor in Sensors)
            ((Sensor)sensor).Value = null;
        CpuId cpuId = _cpuId[0][0];
        GroupAffinity previousAffinity = ThreadAffinity.Set(cpuId.Affinity);
        if (previousAffinity == GroupAffinity.Undefined)
            throw new IOException("The CPU sampling thread affinity could not be established.");
        bool acquired = false;
        try
        {
            acquired = Mutexes.WaitPciBus(10);
            if (!acquired)
                throw new TimeoutException("The CPU thermal PCI read mutex was unavailable.");

            // Indexed read: the signed AMD module writes only the SMN address selector
            // before reading data. No RyzenSMU module or telemetry-mailbox operation is loaded.
            uint raw = _pawnModule.ReadSmn(0x00059800); // THM_TCON_CUR_TMP, upstream Amd17Cpu.
            var sample = DecodeTemperature(raw, cpuId.Name);
            if (sample.Split)
            {
                _coreTemperatureTctl.Value = sample.Tctl;
                _coreTemperatureTdie.Value = sample.Tdie;
                ActivateSensor(_coreTemperatureTctl);
                ActivateSensor(_coreTemperatureTdie);
            }
            else
            {
                _coreTemperatureTctlTdie.Value = sample.Tdie;
                ActivateSensor(_coreTemperatureTctlTdie);
            }
        }
        finally
        {
            try
            {
                if (acquired)
                    Mutexes.ReleasePciBus();
            }
            finally
            {
                if (ThreadAffinity.Set(previousAffinity) == GroupAffinity.Undefined)
                    throw new IOException("The CPU sampling thread affinity could not be restored.");
            }
        }
    }

    internal static (float Tctl, float Tdie, bool Split) DecodeTemperature(uint raw, string name)
    {
        // Preserve the pinned upstream CUR_TEMP bits [31:21], RANGE_SEL[19],
        // TJ_SEL[17:16], and reviewed family/model name-offset semantics.
        bool rangeOffset = (raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000;
        float tctl = ((raw >> 21) * 125) * 0.001f;
        if (rangeOffset)
            tctl -= 49.0f;
        float offset = 0;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.Contains("1600X") || name.Contains("1700X") || name.Contains("1800X"))
                offset = -20.0f;
            else if (name.Contains("Threadripper 19") || name.Contains("Threadripper 29"))
                offset = -27.0f;
            else if (name.Contains("2700X"))
                offset = -10.0f;
        }
        float tdie = tctl + offset;
        if (!float.IsFinite(tdie) || tdie is < 0 or > 125 || tctl is < 0 or > 125)
            throw new InvalidDataException("The AMD CPU temperature is outside the admitted 0–125 °C range.");
        return (tctl, tdie, offset < 0);
    }

    public override void Close()
    {
        try { base.Close(); }
        finally { _pawnModule.Close(); }
    }
}
