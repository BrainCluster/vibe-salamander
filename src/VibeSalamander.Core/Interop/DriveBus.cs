// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeSalamander.Core.Interop;

/// <summary>
/// How a drive is connected (its storage bus): USB, SD card, a mounted disk image, ... Used to tell
/// plugged-in USB drives and memory cards apart from mounted images and internal disks.
/// </summary>
public static class DriveBus
{
    public const int Usb = 0x7;
    public const int Sd = 0xC;
    public const int Mmc = 0xD;
    public const int FileBackedVirtual = 0xF;

    /// <summary>The STORAGE_BUS_TYPE of the drive ("E:\"), or null when it can't be asked.</summary>
    public static int? Query(string driveRoot)
    {
        if (driveRoot.Length < 2 || driveRoot[1] != ':')
            return null;
        using SafeFileHandle handle = CreateFileW($@"\\.\{driveRoot[..2]}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0), AdditionalParameters[1] }
        byte[] query = new byte[12];
        byte[] descriptor = new byte[1024];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, descriptor, descriptor.Length, out int returned, IntPtr.Zero) || returned < 32)
            return null;
        // STORAGE_DEVICE_DESCRIPTOR.BusType is at offset 28
        return BitConverter.ToInt32(descriptor, 28);
    }

    /// <summary>True for USB drives (flash drives, USB disks, card readers) and SD / MMC memory cards.</summary>
    public static bool IsPlugIn(string driveRoot) => Query(driveRoot) is Usb or Sd or Mmc;

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] inBuffer, int inSize,
        byte[] outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);
}
