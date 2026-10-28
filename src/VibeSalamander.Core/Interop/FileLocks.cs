// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Interop;

/// <summary>
/// Which programs have a file open (Windows' Restart Manager, as Explorer's "The action can't be completed because the
/// file is open in ..."), to say it in an error instead of only "being used by another process".
/// </summary>
public static class FileLocks
{
    /// <summary>The error is "the file is used by another process" (or a part of it is locked).</summary>
    public static bool IsLockError(Exception ex) => ex switch
    {
        Win32Exception w => w.NativeErrorCode is 32 or 33,
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };

    /// <summary>The error's message, with the programs that hold the file when it's a lock ("in use by Word, Excel").</summary>
    public static string Explain(Exception ex, params string[] paths)
    {
        if (!IsLockError(ex) || !OperatingSystem.IsWindows())
            return ex.Message;
        IReadOnlyList<string> holders = Holders(paths);
        return holders.Count == 0 ? ex.Message : ex.Message.TrimEnd() + " " + L.F("(in use by {0})", string.Join(", ", holders));
    }

    /// <summary>The names of the programs that have one of the files open (empty when it can't be told).</summary>
    public static IReadOnlyList<string> Holders(IReadOnlyList<string> paths)
    {
        var names = new List<string>();
        if (paths.Count == 0 || !OperatingSystem.IsWindows())
            return names;
        string key = Guid.NewGuid().ToString("N");
        if (RmStartSession(out uint session, 0, key) != 0)
            return names;
        try
        {
            string[] files = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
                return names;
            uint needed = 0, count = 0;
            int result = RmGetList(session, out needed, ref count, null, out _);
            if (result != ERROR_MORE_DATA || needed == 0)
                return names;
            var list = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, list, out _) != 0)
                return names;
            for (int i = 0; i < count; i++)
            {
                string name = list[i].strAppName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        name = System.Diagnostics.Process.GetProcessById((int)list[i].Process.dwProcessId).ProcessName;
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        continue;
                    }
                }
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
            return names;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return names;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public uint dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
        RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, out uint lpdwRebootReasons);
}
