using Microsoft.Win32;

namespace VibeSalamander.Core.Interop;

/// <summary>Disk images (.iso, .img, .vhd, .vhdx) that Windows Explorer mounts as a drive when they are opened.</summary>
public static class DiskImages
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".img", ".vhd", ".vhdx" };

    /// <summary>Explorer's own types: opening them mounts the image (another default app, e.g. a burner, doesn't).</summary>
    private static readonly HashSet<string> ExplorerProgIds = new(StringComparer.OrdinalIgnoreCase) { "Windows.IsoFile", "Windows.VhdFile" };

    /// <summary>True when opening the file with its default app makes Explorer mount it.</summary>
    public static bool MountsInExplorer(string path)
    {
        string extension = Path.GetExtension(path);
        if (!Extensions.Contains(extension))
            return false;
        string? progId = DefaultProgId(extension);
        return progId != null && ExplorerProgIds.Contains(progId);
    }

    /// <summary>The user's choice (Open with → Always), else the machine's default.</summary>
    private static string? DefaultProgId(string extension)
    {
        try
        {
            using RegistryKey? choice = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
            if (choice?.GetValue("ProgId") is string chosen && chosen.Length > 0)
                return chosen;
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(extension);
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The root of every drive that is there now ("C:\", "D:\").</summary>
    public static HashSet<string> DriveRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
                roots.Add(drive.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no drive list: nothing found
        }
        return roots;
    }
}
