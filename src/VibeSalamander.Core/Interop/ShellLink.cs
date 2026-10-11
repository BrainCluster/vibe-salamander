// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace VibeSalamander.Core.Interop;

/// <summary>Resolves Windows shortcuts (.lnk) and Internet shortcuts (.url) for "Go to Shortcut Target" (Ctrl+T).</summary>
public static class ShellLink
{
    /// <summary>The target path of a shortcut, or null when it cannot be resolved (or targets a non-file item).</summary>
    public static string? GetTarget(string shortcutPath)
    {
        string ext = Path.GetExtension(shortcutPath);
        if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return ReadUrl(shortcutPath);
        if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;

        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0 /* STGM_READ */);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string target = sb.ToString();
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (link != null)
                Marshal.ReleaseComObject(link);
        }
    }

    private static string? ReadUrl(string path)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return line[4..].Trim();
        }
        catch (IOException)
        {
        }
        return null;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    /// <summary>The vtable order must match the SDK declaration of IShellLinkW.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>Creates a shortcut (.lnk) at <paramref name="linkPath"/> pointing to <paramref name="target"/> (Paste Shortcut, Ctrl+S).</summary>
    public static void Create(string target, string linkPath)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            var shellLink = (IShellLinkW)link;
            shellLink.SetPath(target);
            string? directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                shellLink.SetWorkingDirectory(directory);
            ((IPersistFile)link).Save(linkPath, true);
        }
        catch (COMException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            if (link != null)
                Marshal.ReleaseComObject(link);
        }
    }
}
