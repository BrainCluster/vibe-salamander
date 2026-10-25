// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime;
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
            _timer.Tick += (_, _) => _ = Task.Run(Collect);
        }
        _timer.Stop();
        _timer.Start(); // after the user has settled in the new folder
    }

    /// <summary>A cleanup now (the window was minimized: nobody notices the pause).</summary>
    public static void CollectNow() => _ = Task.Run(Collect);

    /// <summary>
    /// Collects and compacts, so freed memory goes back to Windows. The pages still in use stay in the working set:
    /// trimming them as well would only make Task Manager's number smaller, and the app would fault them back in
    /// (a small stutter) as soon as it is used again.
    /// </summary>
    private static void Collect()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers(); // WinRT objects let go of their native parts here
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception)
        {
            // only a cleanup: nothing to report
        }
    }
}
