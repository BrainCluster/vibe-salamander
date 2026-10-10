using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeSalamander.Core.Tools;

public enum NtfsAction
{
    Compress,
    Uncompress,
    Encrypt,
    Decrypt,
}

/// <summary>NTFS Compress / Uncompress / Encrypt / Decrypt (Files menu), applied to items and everything inside directories.</summary>
public static class NtfsOperations
{
    /// <summary>Returns (succeeded, failed, first error message).</summary>
    public static Task<(int Succeeded, int Failed, string? FirstError)> ApplyAsync(IReadOnlyList<string> paths, NtfsAction action,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            int ok = 0, failed = 0;
            string? firstError = null;
            foreach (string path in Expand(paths))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    switch (action)
                    {
                        case NtfsAction.Compress: SetCompression(path, true); break;
                        case NtfsAction.Uncompress: SetCompression(path, false); break;
                        case NtfsAction.Encrypt: File.Encrypt(path); break;
                        default: File.Decrypt(path); break;
                    }
                    ok++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
                {
                    failed++;
                    firstError ??= $"{path}: {ex.Message}";
                }
            }
            return (ok, failed, firstError);
        }, ct);

    /// <summary>The items, then the content of directories (directories first, so new files inherit the state).</summary>
    private static IEnumerable<string> Expand(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            yield return path;
            if (!Directory.Exists(path))
                continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                children = Array.Empty<string>();
            }
            foreach (string child in children)
                yield return child;
        }
    }

    private const uint FSCTL_SET_COMPRESSION = 0x9C040;
    private const ushort COMPRESSION_FORMAT_NONE = 0, COMPRESSION_FORMAT_DEFAULT = 1;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_ALL = 0x7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private static void SetCompression(string path, bool compress)
    {
        using SafeFileHandle handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        ushort format = compress ? COMPRESSION_FORMAT_DEFAULT : COMPRESSION_FORMAT_NONE;
        if (!DeviceIoControl(handle, FSCTL_SET_COMPRESSION, ref format, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref ushort inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
