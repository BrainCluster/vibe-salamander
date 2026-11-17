// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeSalamander.Core.Operations.Elevation;

/// <summary>
/// The changes an operation makes on disk: by this process, or by the helper running as administrator when this
/// process isn't allowed to (a copy into Program Files). The copy engine plans, asks and checks; only these few
/// changes are ever done with administrator rights.
/// </summary>
public interface IFileOps
{
    /// <summary>
    /// Copies a file (attributes, times and alternate streams with it). <paramref name="progress"/> gets the total and
    /// the bytes copied so far and returns false to cancel (then <see cref="OperationCanceledException"/> is thrown).
    /// </summary>
    void CopyFile(string source, string target, bool overwrite, Func<long, long, bool> progress);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    /// <summary>Deletes an empty directory, or a link to a directory (not what it points to).</summary>
    void DeleteDirectory(string path);

    void Move(string source, string target, bool isDirectory);

    void SetAttributes(string path, FileAttributes attributes);

    void SetLastWriteTime(string path, DateTime time, bool isDirectory);
}

/// <summary>The work done as administrator, for one transfer: ends (and the helper with it) when disposed.</summary>
public interface IElevatedSession : IFileOps, IDisposable
{
}

/// <summary>Starts the helper that runs as administrator (Windows asks the user first).</summary>
public interface IElevation
{
    /// <summary>
    /// Starts the helper. Throws <see cref="ElevationRefusedException"/> when the user says no in Windows' prompt,
    /// and an <see cref="IOException"/> when it can't be started.
    /// </summary>
    Task<IElevatedSession> StartAsync(CancellationToken cancellationToken);
}

/// <summary>The user said no when Windows asked to allow the change as administrator.</summary>
public sealed class ElevationRefusedException() : Exception("Windows didn't allow the change as administrator.");

/// <summary>Which failures administrator rights could fix.</summary>
public static class ElevationRules
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    /// <summary>
    /// True for "access denied" (a protected folder, a file of the system): not for a file that is in use, missing
    /// or read-only (those are the same for an administrator).
    /// </summary>
    public static bool IsAccessDenied(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        Win32Exception win32 => win32.NativeErrorCode is ERROR_ACCESS_DENIED or ERROR_PRIVILEGE_NOT_HELD,
        IOException io => (io.HResult & 0xFFFF) is ERROR_ACCESS_DENIED or ERROR_PRIVILEGE_NOT_HELD && (io.HResult >> 16 & 0x1FFF) == 7,
        _ => false,
    };

    /// <summary>The Windows error code an exception carries (0 when it has none).</summary>
    public static int ErrorCode(Exception exception) => exception switch
    {
        Win32Exception win32 => win32.NativeErrorCode,
        UnauthorizedAccessException => ERROR_ACCESS_DENIED,
        _ when (exception.HResult >> 16 & 0x1FFF) == 7 => exception.HResult & 0xFFFF, // FACILITY_WIN32
        _ => 0,
    };

    /// <summary>This process already runs as administrator: nothing more to ask for.</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;
}

/// <summary>The changes made by this process.</summary>
public sealed class LocalFileOps : IFileOps
{
    public static readonly LocalFileOps Instance = new();

    private LocalFileOps()
    {
    }

    public void CopyFile(string source, string target, bool overwrite, Func<long, long, bool> progress)
    {
        bool cancelled = false;
        CopyProgressRoutine routine = (total, transferred, _, _, _, _, _, _, _) =>
        {
            if (progress(total, transferred))
                return PROGRESS_CONTINUE;
            cancelled = true;
            return PROGRESS_CANCEL;
        };
        int cancel = 0;
        uint flags = COPY_FILE_ALLOW_DECRYPTED_DESTINATION | (overwrite ? 0 : COPY_FILE_FAIL_IF_EXISTS);
        bool ok = CopyFileEx(LongPath(source), LongPath(target), routine, IntPtr.Zero, ref cancel, flags);
        int error = Marshal.GetLastWin32Error();
        GC.KeepAlive(routine);
        if (!ok)
        {
            if (error == ERROR_REQUEST_ABORTED && cancelled)
                throw new OperationCanceledException();
            throw new Win32Exception(error);
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public void Move(string source, string target, bool isDirectory)
    {
        if (isDirectory)
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public void SetLastWriteTime(string path, DateTime time, bool isDirectory)
    {
        if (isDirectory)
            Directory.SetLastWriteTime(path, time);
        else
            File.SetLastWriteTime(path, time);
    }

    /// <summary>Adds the \\?\ prefix for paths beyond MAX_PATH.</summary>
    internal static string LongPath(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;
    private const uint COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x8;
    private const uint PROGRESS_CONTINUE = 0;
    private const uint PROGRESS_CANCEL = 1;
    internal const int ERROR_REQUEST_ABORTED = 1235;

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize,
        long streamBytesTransferred, uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CopyFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
        IntPtr data, ref int cancel, uint copyFlags);
}
