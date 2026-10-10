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

    /// <summary>Only the first method of IShellLinkW is used; the vtable order must match the SDK declaration.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }
}
