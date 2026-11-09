// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

// OpenBlade modification, 2026-11-07: CPU-temperature-only source subset; see README.OpenBlade.md.

using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace LibreHardwareMonitor.Hardware.Cpu;

public class GenericCpu : Hardware
{
    protected readonly int _coreCount;
    protected readonly CpuId[][] _cpuId;
    protected readonly uint _family;
    protected readonly uint _model;
    protected readonly uint _stepping;

    public GenericCpu(int processorIndex, CpuId[][] cpuId, ISettings settings)
        : base(cpuId[0][0].Name,
               new Identifier(cpuId[0][0].Vendor == Vendor.Intel ? "intelcpu" : "amdcpu",
                              processorIndex.ToString(CultureInfo.InvariantCulture)), settings)
    {
        _cpuId = cpuId;
        _coreCount = cpuId.Length;
        _family = cpuId[0][0].Family;
        _model = cpuId[0][0].Model;
        _stepping = cpuId[0][0].Stepping;
    }

    public CpuId[][] CpuId => _cpuId;
    public override HardwareType HardwareType => HardwareType.Cpu;
    protected string CoreString(int i) => _coreCount == 1 ? "CPU Core" : "CPU Core #" + (i + 1);
    public override void Update() { }
}
