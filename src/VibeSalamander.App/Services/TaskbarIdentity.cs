// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace VibeSalamander.Services;

/// <summary>
/// In a release the program runs from the "lib" folder and the main folder has a small starter
/// (VibeSalamander.exe). Pinning the window to the taskbar would pin lib\VibeSalamander.exe; these
/// window properties make the taskbar pin (and relaunch) the starter in the main folder instead.
/// </summary>
public static class TaskbarIdentity
{
    private const string AppId = "BrainCluster.VibeSalamander";

    public static void Apply(IntPtr hwnd)
    {
        try
        {
            string folder = AppContext.BaseDirectory.TrimEnd('\\');
            if (!string.Equals(Path.GetFileName(folder), "lib", StringComparison.OrdinalIgnoreCase))
                return; // a development build: nothing to point to
            string? main = Path.GetDirectoryName(folder);
            string starter = main == null ? string.Empty : Path.Combine(main, "VibeSalamander.exe");
            if (!File.Exists(starter))
                return;

            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore store) != 0 || store == null)
                return;
            try
            {
                SetString(store, 5, AppId);                        // System.AppUserModel.ID
                SetString(store, 2, $"\"{starter}\"");             // System.AppUserModel.RelaunchCommand
                SetString(store, 4, "Vibe Salamander");            // System.AppUserModel.RelaunchDisplayNameResource
                SetString(store, 3, $"{starter},0");               // System.AppUserModel.RelaunchIconResource
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // the taskbar then simply uses lib\VibeSalamander.exe
        }
    }

    private static void SetString(IPropertyStore store, uint pid, string value)
    {
        var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = pid };
        var variant = new PropVariant { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant.Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
