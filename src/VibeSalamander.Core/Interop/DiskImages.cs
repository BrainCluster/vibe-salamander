// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeSalamander.Core.Interop;

/// <summary>
/// Disk images (.iso, .img, .vhd, .vhdx): mounting them as a drive and unmounting them, without
/// Explorer. Mounting uses the Windows storage cmdlets (Mount-DiskImage); when that isn't possible
/// the shell's "Mount" verb is used instead.
/// </summary>
public static class DiskImages
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".img", ".vhd", ".vhdx" };

    /// <summary>True for .iso, .img, .vhd and .vhdx files.</summary>
    public static bool IsDiskImage(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Mounts the image (or finds where it is mounted already) and returns the root of its drive
    /// ("E:\"). Throws InvalidOperationException with a readable message when it can't be mounted.
    /// </summary>
    public static async Task<string> MountAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$p = $env:VIBE_IMAGE; " +
            "$img = Get-DiskImage -ImagePath $p; " +
            "if (-not $img.Attached) { $img = Mount-DiskImage -ImagePath $p -PassThru; Start-Sleep -Milliseconds 300 }; " +
            "($img | Get-Volume | Where-Object DriveLetter | Select-Object -First 1).DriveLetter";
        (int exit, string output, string error) = await RunPowerShellAsync(script, imagePath, null, cancellationToken);
        string letter = output.Trim();
        if (exit == 0 && letter.Length == 1 && char.IsLetter(letter[0]))
            return char.ToUpperInvariant(letter[0]) + ":\\";

        // no storage cmdlets (or not allowed): the shell's own "Mount" (Explorer may open a window too)
        HashSet<string> before = DriveRoots();
        try
        {
            Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true, Verb = "mount" });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(FirstLine(error) ?? ex.Message, ex);
        }
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            string? added = DriveRoots().Except(before, StringComparer.OrdinalIgnoreCase).OrderBy(r => r).FirstOrDefault();
            if (added != null)
                return added;
        }
        throw new InvalidOperationException(FirstLine(error) ?? "The image was not mounted.");
    }

    /// <summary>Unmounts the image mounted as this drive ("E:\"). Throws InvalidOperationException when it fails.</summary>
    public static async Task UnmountAsync(string driveRoot, CancellationToken cancellationToken = default)
    {
        string letter = driveRoot[..1];
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$img = Get-Volume -DriveLetter $env:VIBE_DRIVE | Get-DiskImage; " +
            "Dismount-DiskImage -ImagePath $img.ImagePath | Out-Null";
        (int exit, _, string error) = await RunPowerShellAsync(script, null, letter, cancellationToken);
        for (int attempt = 0; exit == 0 && attempt < 8; attempt++)
        {
            if (!DriveRoots().Contains(driveRoot))
                return;
            await Task.Delay(250, cancellationToken);
        }

        // fallback: the shell's "Eject" (it unmounts virtual drives)
        try
        {
            Process.Start(new ProcessStartInfo(driveRoot) { UseShellExecute = true, Verb = "eject" });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(FirstLine(error) ?? ex.Message, ex);
        }
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (!DriveRoots().Contains(driveRoot))
                return;
        }
        throw new InvalidOperationException(FirstLine(error) ?? "The drive could not be unmounted.");
    }

    private static string? FirstLine(string text)
    {
        string? line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    /// <summary>Runs a short PowerShell script hidden; paths go in environment variables (no quoting problems).</summary>
    private static async Task<(int Exit, string Output, string Error)> RunPowerShellAsync(string script, string? imagePath,
        string? driveLetter, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            info.ArgumentList.Add(arg);
        if (imagePath != null)
            info.Environment["VIBE_IMAGE"] = imagePath;
        if (driveLetter != null)
            info.Environment["VIBE_DRIVE"] = driveLetter;
        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell could not be started.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    /// <summary>The root of every drive that is there now ("C:\", "D:\").</summary>
    public static HashSet<string> DriveRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
                roots.Add(drive.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no drive list: nothing found
        }
        return roots;
    }

    // ---- is a drive a mounted image? ----------------------------------------------------------

    /// <summary>True when the drive ("E:\") is a mounted disk image (its storage bus is "file backed virtual").</summary>
    public static bool IsMountedImage(string driveRoot)
    {
        if (driveRoot.Length < 2 || driveRoot[1] != ':')
            return false;
        using SafeFileHandle handle = CreateFileW($@"\\.\{driveRoot[..2]}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0), AdditionalParameters[1] }
        byte[] query = new byte[12];
        byte[] descriptor = new byte[1024];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, descriptor, descriptor.Length, out int returned, IntPtr.Zero) || returned < 32)
            return false;
        // STORAGE_DEVICE_DESCRIPTOR.BusType is at offset 28
        return BitConverter.ToInt32(descriptor, 28) == BusTypeFileBackedVirtual;
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int BusTypeFileBackedVirtual = 0xF;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] inBuffer, int inSize,
        byte[] outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);
}
