// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeSalamander.Core.Interop;

/// <summary>
/// Which file or folder a path really leads to. Two different paths can lead to the same one (a junction, a hard
/// link, a subst drive, a share of this computer); comparing the paths' text doesn't see it, and a copy "onto
/// another file" that is really the source itself would destroy it.
/// </summary>
public static class FileIdentity
{
    /// <summary>True when both paths lead to the same file or folder; false when they don't or when it can't be told.</summary>
    public static bool IsSame(string a, string b) => Id(a) is { } x && Id(b) is { } y && x == y;

    /// <summary>The volume and the file's number on it, or null when the path can't be opened.</summary>
    public static (ulong Volume, ulong High, ulong Low)? Id(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        using SafeFileHandle handle = Open(path);
        if (handle.IsInvalid)
            return null;
        // 128-bit ids (ReFS needs them); FAT and older systems only have the 64-bit one
        if (GetFileInformationByHandleEx(handle, FileIdInfo, out FILE_ID_INFO id, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
            return (id.VolumeSerialNumber, id.FileIdHigh, id.FileIdLow);
        if (GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
            return (info.VolumeSerialNumber, 0, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        return null;
    }

    /// <summary>
    /// The path Windows itself uses for a file or folder: links, subst drives and short names resolved
    /// ("C:\Users\me\Docs" for "X:\Docs" when X: is "subst X: C:\Users\me"). Null when it can't be told.
    /// </summary>
    public static string? FinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        using SafeFileHandle handle = Open(path);
        if (handle.IsInvalid)
            return null;
        var buffer = new char[512];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length > buffer.Length)
            return null;
        string final = new(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
            return final[4..];
        return final;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> or inside it once links and subst drives are
    /// resolved (false when either can't be told). <paramref name="path"/> need not exist: its nearest existing parent
    /// is used.
    /// </summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        string? existing = path;
        while (existing != null && !Directory.Exists(existing) && !File.Exists(existing))
            existing = Path.GetDirectoryName(existing);
        if (existing == null || FinalPath(existing) is not { } finalPath || FinalPath(folder) is not { } finalFolder)
            return false;
        finalFolder = finalFolder.TrimEnd('\\');
        return string.Equals(finalPath.TrimEnd('\\'), finalFolder, StringComparison.OrdinalIgnoreCase) ||
               finalPath.StartsWith(finalFolder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a file or folder only to ask about it: no reading, so files other programs hold open work too.</summary>
    private static SafeFileHandle Open(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length >= 248 && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        return CreateFileW(full, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
    }

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // needed to open a folder
    private const int FileIdInfo = 18; // FILE_INFO_BY_HANDLE_CLASS

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow; // FILE_ID_128: 16 bytes
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_ID_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
