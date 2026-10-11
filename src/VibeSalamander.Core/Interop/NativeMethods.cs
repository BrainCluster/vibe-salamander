// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace VibeSalamander.Core.Interop;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable,
                                                   out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>"\\server\share" for a mapped drive root ("Z:\"), or null.</summary>
    internal static string? GetRemoteName(string root)
    {
        var sb = new StringBuilder(512);
        int len = sb.Capacity;
        return WNetGetConnection(root.TrimEnd('\\'), sb, ref len) == 0 ? sb.ToString() : null;
    }

    // ---- shell file operations -------------------------------------------------------------

    internal const uint FO_MOVE = 0x0001;
    internal const uint FO_COPY = 0x0002;
    internal const uint FO_DELETE = 0x0003;
    internal const uint FO_RENAME = 0x0004;

    internal const ushort FOF_MULTIDESTFILES = 0x0001;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    // 64-bit layout (the app ships for x64 and ARM64 only; x86 would need Pack = 1).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    internal const uint SHOP_FILEPATH = 0x2;

    // ---- Open With dialog ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public uint oaifInFlags;
    }

    internal const uint OAIF_ALLOW_REGISTRATION = 0x1;
    internal const uint OAIF_EXEC = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);
}
