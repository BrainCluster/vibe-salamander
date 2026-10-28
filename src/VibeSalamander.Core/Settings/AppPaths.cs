// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Settings;

/// <summary>
/// Where Vibe Salamander keeps its data: settings and plugins in %AppData%\VibeSalamander, logs in
/// %LocalAppData%\VibeSalamander\Logs. The environment variable VIBE_SALAMANDER_HOME moves all of it into one folder
/// (the automatic UI tests run each test with its own settings, never touching the user's), and a "portable.flag"
/// file next to the program keeps it all in a "Data" folder beside the program (the zips come that way).
/// </summary>
public static class AppPaths
{
    public const string HomeVariable = "VIBE_SALAMANDER_HOME";

    /// <summary>The file next to VibeSalamander.exe that makes it portable: everything is kept in "Data" beside it.</summary>
    public const string PortableFlag = "portable.flag";

    /// <summary>The file the installer puts next to VibeSalamander.exe (updates then come as the installer).</summary>
    public const string InstalledFlag = "installed.flag";

    /// <summary>
    /// The folder with the starter VibeSalamander.exe, the documents and "lib" (the program runs from lib); for a
    /// build that runs straight from its output folder, that folder.
    /// </summary>
    public static string MainFolder { get; } = FindMainFolder(AppContext.BaseDirectory);

    internal static string FindMainFolder(string programFolder)
    {
        string folder = Path.TrimEndingDirectorySeparator(programFolder);
        string? parent = Path.GetDirectoryName(folder);
        return Path.GetFileName(folder).Equals("lib", StringComparison.OrdinalIgnoreCase) && parent != null &&
               File.Exists(Path.Combine(parent, "VibeSalamander.exe"))
            ? parent
            : folder;
    }

    private static string? Override =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? Path.GetFullPath(home)
        : IsPortable ? PortableData
        : null;

    /// <summary>
    /// Portable mode: an empty "portable.flag" next to VibeSalamander.exe keeps the settings, plugins, logs and
    /// caches in a "Data" folder beside it (a USB stick), nothing in the user's profile.
    /// </summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(MainFolder, PortableFlag)) ||
                                             File.Exists(Path.Combine(AppContext.BaseDirectory, PortableFlag));

    /// <summary>Installed with the installer (not a zip).</summary>
    public static bool IsInstalled { get; } = File.Exists(Path.Combine(MainFolder, InstalledFlag));

    /// <summary>The portable data folder.</summary>
    public static string PortableData => Path.Combine(MainFolder, "Data");

    /// <summary>Where the settings are when the program isn't portable.</summary>
    public static string RoamingHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSalamander");

    /// <summary>Settings (settings.json) and the user's plugins.</summary>
    public static string Home => Override ?? RoamingHome;

    /// <summary>The web view's own data (the viewer's previews, the editor): cache, not settings.</summary>
    public static string WebView => Override is { } home
        ? Path.Combine(home, "WebView2")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSalamander", "WebView2");

    /// <summary>The error logs.</summary>
    public static string Logs => Override is { } home
        ? Path.Combine(home, "Logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSalamander", "Logs");

    /// <summary>
    /// The first start of a portable copy: the settings and plugins used so far (in %AppData%) are copied into Data,
    /// so moving to the portable zip loses nothing. Returns true when something was copied.
    /// </summary>
    public static bool AdoptRoamingSettings()
    {
        if (!IsPortable || Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 })
            return false;
        return CopySettings(RoamingHome, PortableData);
    }

    internal static bool CopySettings(string from, string to)
    {
        try
        {
            if (File.Exists(Path.Combine(to, "settings.json")) || !File.Exists(Path.Combine(from, "settings.json")))
                return false;
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(from, file);
                if (relative.StartsWith("Logs", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("WebView2", StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // starts with the defaults; the old settings stay where they were
        }
    }
}
