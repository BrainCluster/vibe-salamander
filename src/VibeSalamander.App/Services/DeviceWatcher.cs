// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace VibeSalamander.Services;

/// <summary>
/// Tells when drives appear and disappear: a USB drive plugged in or pulled out, a memory card
/// inserted, a disk image mounted. Windows sends WM_DEVICECHANGE with the drive letters to every
/// top-level window; this listens on the main window. Network drives are left out.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private const uint WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 2;
    private const short DBTF_NET = 0x0002;
    private static readonly UIntPtr SubclassId = new(0x56534457); // "VSDW"

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc; // kept alive as long as the window uses it
    private bool _disposed;

    /// <summary>Drive roots ("E:\") that appeared.</summary>
    public event EventHandler<IReadOnlyList<string>>? Arrived;

    /// <summary>Drive roots ("E:\") that are gone.</summary>
    public event EventHandler<IReadOnlyList<string>>? Removed;

    public DeviceWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WindowProc;
        SetWindowSubclass(hwnd, _proc, SubclassId, UIntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_DEVICECHANGE && lParam != IntPtr.Zero && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
        {
            try
            {
                // DEV_BROADCAST_VOLUME { dbch_size, dbch_devicetype, dbch_reserved, dbcv_unitmask, dbcv_flags }
                if (Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME && (Marshal.ReadInt16(lParam, 16) & DBTF_NET) == 0)
                {
                    int mask = Marshal.ReadInt32(lParam, 12);
                    var roots = new List<string>();
                    for (int bit = 0; bit < 26; bit++)
                        if ((mask & (1 << bit)) != 0)
                            roots.Add((char)('A' + bit) + ":\\");
                    if (roots.Count > 0)
                        ((int)wParam == DBT_DEVICEARRIVAL ? Arrived : Removed)?.Invoke(this, roots);
                }
            }
            catch (Exception)
            {
                // never let a problem here reach the window's message loop
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
