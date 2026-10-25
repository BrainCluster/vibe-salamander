// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VibeSalamander.Services;

/// <summary>
/// Makes the main window the owner of a window (Find, Configuration, plugin windows): an owned window is always
/// above its owner, also when the owner is activated or "always on top", is minimized with it and has no taskbar
/// button of its own.
/// </summary>
public static class WindowOwner
{
    private const int GwlpHwndParent = -8;

    public static void Set(Window window, IntPtr owner)
    {
        if (owner == IntPtr.Zero)
            return;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
