// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// OpenBlade modification, 2026-11-07: retain only the reviewed typed SMN temperature read.
namespace LibreHardwareMonitor.PawnIo;

internal sealed class AmdFamily17
{
    private readonly PawnIo _pawnIo = PawnIo.LoadModuleFromResource(typeof(AmdFamily17).Assembly,
        "LibreHardwareMonitor.Resources.PawnIo.AMDFamily17.bin");

    internal uint ReadSmn(uint offset) => (uint)_pawnIo.Execute("ioctl_read_smn", [offset], 1)[0];
    internal void Close() => _pawnIo.Close();
}
