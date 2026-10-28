// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeSalamander.Core.Interop;

/// <summary>What happens when the last transfer has finished (the transfer panel's "When done").</summary>
public enum WhenDone
{
    Nothing,
    CloseApp,
    Lock,
    Sleep,
    Hibernate,
    ShutDown,
}

/// <summary>Lock, sleep, hibernate and shut down the computer.</summary>
public static class PowerActions
{
    /// <summary>"Sleeping in {0} s" and the like (the app's own close is done by the app).</summary>
    public static string Countdown(WhenDone action) => action switch
    {
        WhenDone.CloseApp => "Vibe Salamander closes in {0} s.",
        WhenDone.Lock => "The computer is locked in {0} s.",
        WhenDone.Sleep => "The computer goes to sleep in {0} s.",
        WhenDone.Hibernate => "The computer hibernates in {0} s.",
        WhenDone.ShutDown => "The computer shuts down in {0} s.",
        _ => string.Empty,
    };

    public static string Name(WhenDone action) => action switch
    {
        WhenDone.CloseApp => "Close Vibe Salamander",
        WhenDone.Lock => "Lock the computer",
        WhenDone.Sleep => "Sleep",
        WhenDone.Hibernate => "Hibernate",
        WhenDone.ShutDown => "Shut down",
        _ => "Nothing",
    };

    /// <summary>Does it (not CloseApp, which is the app's). False when Windows refused.</summary>
    public static bool Run(WhenDone action)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            switch (action)
            {
                case WhenDone.Lock:
                    return LockWorkStation();
                case WhenDone.Sleep:
                    return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
                case WhenDone.Hibernate:
                    return SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
                case WhenDone.ShutDown:
                    // Windows' own shutdown.exe, by its full path and with arguments (no shell)
                    string shutdown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
                    using (Process.Start(new ProcessStartInfo(shutdown) { ArgumentList = { "/s", "/t", "0" }, UseShellExecute = false, CreateNoWindow = true }))
                        return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
