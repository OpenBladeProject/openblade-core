// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// OpenBlade modification, 2026-11-07: fail explicitly on native failure and exact output-length mismatch;
// keep raw PawnIO execution internal and close every failed module-load handle.
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using PInvoke = Windows.Win32.PInvoke;

namespace LibreHardwareMonitor.PawnIo;

internal sealed class PawnIo
{
    private const uint DEVICE_TYPE = 41394u << 16;
    private const int FN_NAME_LENGTH = 32;
    private const uint IOCTL_EXECUTE = DEVICE_TYPE | (0x841 << 2);
    private const uint IOCTL_LOAD_BINARY = DEVICE_TYPE | (0x821 << 2);
    private readonly SafeFileHandle _handle;

    private PawnIo(SafeFileHandle handle) => _handle = handle;
    internal bool IsLoaded => _handle is { IsInvalid: false, IsClosed: false };

    internal static unsafe PawnIo LoadModuleFromResource(Assembly assembly, string resourceName)
    {
        SafeFileHandle handle = PInvoke.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
            (uint)FileAccess.ReadWrite, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
        try
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The PawnIO device could not be opened.");
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException("The reviewed signed CPU module resource is absent.");
            using MemoryStream memory = new();
            stream.CopyTo(memory);
            byte[] bin = memory.ToArray();
            fixed (byte* input = bin)
            {
                if (!PInvoke.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), IOCTL_LOAD_BINARY,
                        input, (uint)bin.Length, null, 0u, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The reviewed signed CPU module could not be loaded.");
            }
            return new PawnIo(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void Close() => _handle.Dispose();

    internal unsafe long[] Execute(string name, long[] input, int outLength)
    {
        if (!IsLoaded)
            throw new IOException("The reviewed signed CPU module is not loaded.");
        if (outLength < 0 || outLength > 1024)
            throw new ArgumentOutOfRangeException(nameof(outLength));
        byte[] output = new byte[outLength * sizeof(long)];
        byte[] request = new byte[input.Length * sizeof(long) + FN_NAME_LENGTH];
        Buffer.BlockCopy(Encoding.ASCII.GetBytes(name), 0, request, 0, Math.Min(FN_NAME_LENGTH - 1, name.Length));
        Buffer.BlockCopy(input, 0, request, FN_NAME_LENGTH, input.Length * sizeof(long));
        uint returned = 0;
        fixed (byte* requestPtr = request, outputPtr = output)
        {
            bool success = PInvoke.DeviceIoControl((HANDLE)_handle.DangerousGetHandle(), IOCTL_EXECUTE,
                requestPtr, (uint)request.Length, outputPtr, (uint)output.Length, &returned, null);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            ValidateExecutionResult(success, returned, (uint)output.Length, error);
        }
        long[] values = new long[outLength];
        Buffer.BlockCopy(output, 0, values, 0, output.Length);
        return values;
    }

    internal static void ValidateExecutionResult(bool success, uint returned, uint expected, int error)
    {
        if (!success)
            throw new Win32Exception(error, "The CPU telemetry IOCTL failed.");
        if (returned != expected)
            throw new InvalidDataException("The CPU telemetry IOCTL returned an unexpected output length.");
    }
}
