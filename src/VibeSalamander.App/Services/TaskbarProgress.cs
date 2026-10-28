// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace VibeSalamander.Services;

/// <summary>The progress shown on the app's taskbar button (Windows' ITaskbarList3), as Explorer's copies show it.</summary>
public sealed class TaskbarProgress
{
    public enum State
    {
        None = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    private readonly IntPtr _window;
    private ITaskbarList3? _taskbar;
    private State _state = State.None;
    private int _percent = -1;

    public TaskbarProgress(IntPtr window) => _window = window;

    private ITaskbarList3? Taskbar
    {
        get
        {
            if (_taskbar != null)
                return _taskbar;
            try
            {
                var list = (ITaskbarList3)new TaskbarListClass();
                list.HrInit();
                _taskbar = list;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _taskbar = null; // no taskbar (Windows Server Core, a test machine): nothing shown
            }
            return _taskbar;
        }
    }

    /// <summary>Shows a state and (for Normal, Paused and Error) how far: 0..100.</summary>
    public void Set(State state, double percent = 0)
    {
        int whole = (int)Math.Round(Math.Clamp(percent, 0, 100));
        if (state == _state && (whole == _percent || state is State.None or State.Indeterminate))
            return;
        try
        {
            ITaskbarList3? taskbar = Taskbar;
            if (taskbar == null)
                return;
            if (state != _state)
                taskbar.SetProgressState(_window, (int)state);
            if (state is State.Normal or State.Paused or State.Error)
                taskbar.SetProgressValue(_window, (ulong)whole, 100);
            _state = state;
            _percent = whole;
        }
        catch (COMException)
        {
            // the taskbar restarted: next time
            _taskbar = null;
        }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3 (only the progress)
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }
}
