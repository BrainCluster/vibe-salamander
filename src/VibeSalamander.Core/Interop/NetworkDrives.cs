// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace VibeSalamander.Core.Interop;

/// <summary>Connect / Disconnect Network Drive (F11 / F12) and the shared directories of this computer (Ctrl+Shift+F9).</summary>
public static class NetworkDrives
{
    private const uint RESOURCETYPE_DISK = 1;

    /// <summary>The Windows "Map Network Drive" dialog. True when a drive was connected.</summary>
    public static bool ShowConnectDialog(IntPtr owner) => WNetConnectionDialog(owner, RESOURCETYPE_DISK) == 0;

    /// <summary>The Windows "Disconnect Network Drives" dialog. True when a drive was disconnected.</summary>
    public static bool ShowDisconnectDialog(IntPtr owner) => WNetDisconnectDialog(owner, RESOURCETYPE_DISK) == 0;

    public sealed record Share(string Name, string Remark, string UncPath);

    /// <summary>Disk shares of this computer (without the administrative ones such as C$ and ADMIN$).</summary>
    public static Task<IReadOnlyList<Share>> GetLocalSharesAsync() => Task.Run<IReadOnlyList<Share>>(() =>
    {
        var shares = new List<Share>();
        int resume = 0;
        int rc = NetShareEnum(null, 1, out IntPtr buffer, -1, out int read, out _, ref resume);
        if (rc != 0 || buffer == IntPtr.Zero)
            return shares;
        try
        {
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            string machine = Environment.MachineName;
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * size);
                bool special = (info.shi1_type & STYPE_SPECIAL) != 0;
                if ((info.shi1_type & 0xFF) != STYPE_DISKTREE || special || info.shi1_netname == null)
                    continue;
                shares.Add(new Share(info.shi1_netname, info.shi1_remark ?? string.Empty, $@"\\{machine}\{info.shi1_netname}"));
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
        return shares.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    private const uint STYPE_DISKTREE = 0;
    private const uint STYPE_SPECIAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string? shi1_netname;
        public uint shi1_type;
        public string? shi1_remark;
    }

    [DllImport("mpr.dll")]
    private static extern int WNetConnectionDialog(IntPtr hwnd, uint type);

    [DllImport("mpr.dll")]
    private static extern int WNetDisconnectDialog(IntPtr hwnd, uint type);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string? serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
