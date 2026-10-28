// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace VibeSalamander.Core.FileSystem;

/// <summary>Drive Information (Ctrl+F1): volume details of the drive or share a path is on.</summary>
public sealed class DriveInformation
{
    public required string Root { get; init; }
    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public DriveType Type { get; init; }
    public uint SerialNumber { get; init; }
    public uint MaxComponentLength { get; init; }
    public uint Flags { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    /// <summary>Free space available to this user (quotas).</summary>
    public long AvailableBytes { get; init; }
    public uint BytesPerCluster { get; init; }

    public string SerialText => $"{SerialNumber >> 16:X4}-{SerialNumber & 0xFFFF:X4}";
    public int UsedPercent => TotalBytes > 0 ? (int)Math.Round(100.0 * (TotalBytes - FreeBytes) / TotalBytes) : 0;

    private const uint FILE_CASE_SENSITIVE_SEARCH = 0x1, FILE_FILE_COMPRESSION = 0x10, FILE_SUPPORTS_ENCRYPTION = 0x20000,
                       FILE_READ_ONLY_VOLUME = 0x80000, FILE_SUPPORTS_HARD_LINKS = 0x400000;

    /// <summary>Notable capabilities ("Compression, Encryption, Hard links").</summary>
    public string Features
    {
        get
        {
            var parts = new List<string>();
            if ((Flags & FILE_FILE_COMPRESSION) != 0) parts.Add("compression");
            if ((Flags & FILE_SUPPORTS_ENCRYPTION) != 0) parts.Add("encryption");
            if ((Flags & FILE_SUPPORTS_HARD_LINKS) != 0) parts.Add("hard links");
            if ((Flags & FILE_CASE_SENSITIVE_SEARCH) != 0) parts.Add("case-sensitive names");
            if ((Flags & FILE_READ_ONLY_VOLUME) != 0) parts.Add("read-only");
            return parts.Count == 0 ? "-" : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
        }
    }

    /// <summary>The volume root of a path: "C:\" or "\\server\share\".</summary>
    public static string? RootOf(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return null;
        return root.EndsWith('\\') ? root : root + "\\";
    }

    /// <summary>The drive is write-protected (a locked SD card, a read-only volume): nothing can be written to it.</summary>
    public static bool IsWriteProtected(string path)
    {
        if (!OperatingSystem.IsWindows() || RootOf(path) is not { } root)
            return false;
        const uint FILE_READ_ONLY_VOLUME = 0x00080000;
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        return GetVolumeInformationW(root, label, label.Capacity, out _, out _, out uint flags, fs, fs.Capacity) &&
               (flags & FILE_READ_ONLY_VOLUME) != 0 && (DriveType)GetDriveTypeW(root) != DriveType.CDRom;
    }

    public static Task<DriveInformation?> QueryAsync(string path) => Task.Run(() =>
    {
        string? root = RootOf(path);
        if (root == null)
            return null;
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        bool volumeOk = GetVolumeInformationW(root, label, label.Capacity, out uint serial, out uint maxLength, out uint flags, fs, fs.Capacity);
        bool spaceOk = GetDiskFreeSpaceExW(root, out ulong available, out ulong total, out ulong free);
        if (!volumeOk && !spaceOk)
            return null;
        uint cluster = GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? sectorsPerCluster * bytesPerSector : 0;
        return new DriveInformation
        {
            Root = root,
            Label = volumeOk ? label.ToString() : string.Empty,
            FileSystem = volumeOk ? fs.ToString() : string.Empty,
            Type = (DriveType)GetDriveTypeW(root),
            SerialNumber = volumeOk ? serial : 0,
            MaxComponentLength = volumeOk ? maxLength : 0,
            Flags = volumeOk ? flags : 0,
            TotalBytes = spaceOk ? (long)total : 0,
            FreeBytes = spaceOk ? (long)free : 0,
            AvailableBytes = spaceOk ? (long)available : 0,
            BytesPerCluster = cluster,
        };
    });

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);
}
