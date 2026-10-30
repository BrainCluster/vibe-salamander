// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace VibeSalamander.Services;

/// <summary>
/// The Windows Explorer context menu for files, folders and drives (IContextMenu), as shown by
/// Salamander on right-click / Shift+F10. Owner-drawn submenus ("Send to", "Open with", …) work
/// because the menu messages are forwarded to IContextMenu2/3 while the menu is open.
/// </summary>
public static class ShellContextMenu
{
    /// <summary>
    /// Shows the menu for items that live in the same folder. Returns the canonical verb the user
    /// chose when the caller should handle it itself ("rename"), otherwise null.
    /// </summary>
    public static string? Show(IntPtr hwnd, IReadOnlyList<string> paths, int screenX, int screenY, bool extendedVerbs)
    {
        if (paths.Count == 0)
            return null;

        var pidls = new List<IntPtr>();
        IntPtr parentPtr = IntPtr.Zero, contextMenuPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? menu = null;
        SubclassProc? subclass = null;
        try
        {
            foreach (string path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                    return null;
                pidls.Add(pidl);
            }

            Guid iidShellFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], ref iidShellFolder, out parentPtr, out _) != 0)
                return null;
            parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);

            IntPtr[] children = pidls.Select(ILFindLastID).ToArray();
            Guid iidContextMenu = typeof(IContextMenu).GUID;
            if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidContextMenu, IntPtr.Zero, out contextMenuPtr) != 0)
                return null;
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);

            hMenu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags) < 0)
                return null;

            IContextMenu2? menu2 = menu as IContextMenu2;
            IContextMenu3? menu3 = menu as IContextMenu3;
            subclass = (IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) =>
            {
                if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    if (menu3 != null && menu3.HandleMenuMsg2(msg, wParam, lParam, out IntPtr result) == 0)
                        return result;
                    if (menu2 != null && menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                        return IntPtr.Zero;
                }
                return DefSubclassProc(h, msg, wParam, lParam);
            };
            SetWindowSubclass(hwnd, subclass, SubclassId, UIntPtr.Zero);

            SetForegroundWindow(hwnd);
            int command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
            RemoveWindowSubclass(hwnd, subclass, SubclassId);

            if (command < FirstCommand)
                return null;
            uint offset = (uint)command - FirstCommand;
            string verb = GetVerb(menu, offset);
            if (verb.Equals("rename", StringComparison.OrdinalIgnoreCase))
                return "rename";

            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectoryW = Path.GetDirectoryName(paths[0]),
                nShow = SW_SHOWNORMAL,
                ptInvoke = new POINT { X = screenX, Y = screenY },
            };
            menu.InvokeCommand(ref invoke);
            return verb.Length > 0 ? verb : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            GC.KeepAlive(subclass);
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menu != null) Marshal.ReleaseComObject(menu);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (contextMenuPtr != IntPtr.Zero) Marshal.Release(contextMenuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (IntPtr pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// Explorer's menu for the empty area of a folder (its background): View and Sort are left to Vibe Salamander,
    /// the rest (Paste, Paste shortcut, New, Properties, the extensions' items) works as in Explorer. False when the
    /// folder has no such menu (the caller shows the folder's own menu then).
    /// </summary>
    public static bool ShowBackground(IntPtr hwnd, string folder, int screenX, int screenY, bool extendedVerbs)
    {
        IntPtr pidl = IntPtr.Zero, desktopPtr = IntPtr.Zero, folderPtr = IntPtr.Zero, contextMenuPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
        IShellFolder? desktop = null, shellFolder = null;
        IContextMenu? menu = null;
        SubclassProc? subclass = null;
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;
            if (SHGetDesktopFolder(out desktopPtr) != 0)
                return false;
            desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
            Guid iidShellFolder = typeof(IShellFolder).GUID;
            if (desktop.BindToObject(pidl, IntPtr.Zero, ref iidShellFolder, out folderPtr) != 0 || folderPtr == IntPtr.Zero)
                return false;
            shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Guid iidContextMenu = typeof(IContextMenu).GUID;
            if (shellFolder.CreateViewObject(hwnd, ref iidContextMenu, out contextMenuPtr) != 0 || contextMenuPtr == IntPtr.Zero)
                return false;
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);

            hMenu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags) < 0 || GetMenuItemCount(hMenu) <= 0)
                return false;

            IContextMenu2? menu2 = menu as IContextMenu2;
            IContextMenu3? menu3 = menu as IContextMenu3;
            subclass = (IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) =>
            {
                if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    if (menu3 != null && menu3.HandleMenuMsg2(msg, wParam, lParam, out IntPtr result) == 0)
                        return result;
                    if (menu2 != null && menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                        return IntPtr.Zero;
                }
                return DefSubclassProc(h, msg, wParam, lParam);
            };
            SetWindowSubclass(hwnd, subclass, SubclassId, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            int command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
            RemoveWindowSubclass(hwnd, subclass, SubclassId);
            if (command < FirstCommand)
                return true; // shown, nothing chosen

            uint offset = (uint)command - FirstCommand;
            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectoryW = folder,
                nShow = SW_SHOWNORMAL,
                ptInvoke = new POINT { X = screenX, Y = screenY },
            };
            menu.InvokeCommand(ref invoke);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            GC.KeepAlive(subclass);
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menu != null) Marshal.ReleaseComObject(menu);
            if (shellFolder != null) Marshal.ReleaseComObject(shellFolder);
            if (desktop != null) Marshal.ReleaseComObject(desktop);
            if (contextMenuPtr != IntPtr.Zero) Marshal.Release(contextMenuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (desktopPtr != IntPtr.Zero) Marshal.Release(desktopPtr);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// The Explorer "New" menu (folder, shortcut, text document, Word document, ...) for a directory, shown at
    /// a screen position. Returns true when the user chose an item (the shell then creates it).
    /// </summary>
    public static bool ShowNewMenu(IntPtr hwnd, string directory, int screenX, int screenY)
    {
        IntPtr pidl = IntPtr.Zero, hMenu = IntPtr.Zero;
        object? newMenu = null;
        SubclassProc? subclass = null;
        try
        {
            if (SHParseDisplayName(directory, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;
            Type? type = Type.GetTypeFromCLSID(new Guid("D969A300-E7FF-11d0-A93B-00A0C90F2719"));
            if (type == null)
                return false;
            newMenu = Activator.CreateInstance(type);
            if (newMenu is not IShellExtInit init || newMenu is not IContextMenu menu)
                return false;
            if (init.Initialize(pidl, IntPtr.Zero, IntPtr.Zero) != 0)
                return false;

            hMenu = CreatePopupMenu();
            if (menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, CMF_NORMAL) < 0)
                return false;
            // the "New" item is a submenu: show its items directly
            IntPtr popup = GetSubMenu(hMenu, 0);
            if (popup == IntPtr.Zero)
                popup = hMenu;

            IContextMenu2? menu2 = newMenu as IContextMenu2;
            IContextMenu3? menu3 = newMenu as IContextMenu3;
            subclass = (IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) =>
            {
                if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    if (menu3 != null && menu3.HandleMenuMsg2(msg, wParam, lParam, out IntPtr result) == 0)
                        return result;
                    if (menu2 != null && menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                        return IntPtr.Zero;
                }
                return DefSubclassProc(h, msg, wParam, lParam);
            };
            SetWindowSubclass(hwnd, subclass, SubclassId, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            int command = TrackPopupMenuEx(popup, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
            RemoveWindowSubclass(hwnd, subclass, SubclassId);
            if (command < FirstCommand)
                return false;

            uint offset = (uint)command - FirstCommand;
            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectoryW = directory,
                nShow = SW_SHOWNORMAL,
                ptInvoke = new POINT { X = screenX, Y = screenY },
            };
            return menu.InvokeCommand(ref invoke) == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            GC.KeepAlive(subclass);
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (newMenu != null && Marshal.IsComObject(newMenu)) Marshal.ReleaseComObject(newMenu);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Screen position of the mouse cursor (for menus opened by right-click).</summary>
    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out POINT p);
        return (p.X, p.Y);
    }

    /// <summary>Converts a point in window client coordinates (physical pixels) to screen coordinates.</summary>
    public static (int X, int Y) ClientToScreen(IntPtr hwnd, int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        ClientToScreen(hwnd, ref p);
        return (p.X, p.Y);
    }

    private static string GetVerb(IContextMenu menu, uint offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, 260) != 0)
                return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty; // some handlers throw for commands without verbs
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- interop -----------------------------------------------------------------------------

    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;
    private static readonly UIntPtr SubclassId = (UIntPtr)0x5653; // "VS"

    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_EXPLORE = 0x4;
    private const uint CMF_CANRENAME = 0x10;
    private const uint CMF_EXTENDEDVERBS = 0x100;
    private const uint GCS_VERBW = 0x4;
    private const uint CMIC_MASK_UNICODE = 0x4000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const int SW_SHOWNORMAL = 1;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint WM_DRAWITEM = 0x2B;
    private const uint WM_MEASUREITEM = 0x2C;
    private const uint WM_INITMENUPOPUP = 0x117;
    private const uint WM_MENUCHAR = 0x120;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E8-0000-0000-C000-000000000046")]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr pdtobj, IntPtr hkeyProgID);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IntPtr ppshf);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
