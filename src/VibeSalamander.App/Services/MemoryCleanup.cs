// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VibeSalamander.Services;

/// <summary>
/// Gives memory back after something big went away: the listing of a folder with thousands of files,
/// a closed window, or the whole window being minimized. .NET would reclaim it on its own only later,
/// so Task Manager would keep showing the old peak.
/// </summary>
public static class MemoryCleanup
{
    private static DispatcherQueueTimer? _timer;

    /// <summary>A cleanup a moment from now (calls close together make one cleanup).</summary>
    public static void Schedule()
    {
        DispatcherQueue? dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher == null)
            return;
        if (_timer == null)
        {
            _timer = dispatcher.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (_, _) => _ = Task.Run(() => Collect(trimWorkingSet: false));
        }
        _timer.Stop();
        _timer.Start(); // after the user has settled in the new folder
    }

    /// <summary>A cleanup now, and the unused pages handed back to Windows (the window was minimized).</summary>
    public static void Trim() => _ = Task.Run(() => Collect(trimWorkingSet: true));

    private static void Collect(bool trimWorkingSet)
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers(); // WinRT objects let go of their native parts here
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (trimWorkingSet)
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception)
        {
            // only a cleanup: nothing to report
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
