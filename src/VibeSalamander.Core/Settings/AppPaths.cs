// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Settings;

/// <summary>
/// Where Vibe Salamander keeps its data: settings and plugins in %AppData%\VibeSalamander, logs in
/// %LocalAppData%\VibeSalamander\Logs. The environment variable VIBE_SALAMANDER_HOME moves all of it into one folder
/// (the automatic UI tests run each test with its own settings, never touching the user's), and a "portable.flag"
/// file next to the program keeps it all beside the program.
/// </summary>
public static class AppPaths
{
    public const string HomeVariable = "VIBE_SALAMANDER_HOME";

    /// <summary>The file next to the program that makes it portable: everything is kept in "Data" beside it.</summary>
    public const string PortableFlag = "portable.flag";

    private static string? Override =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? Path.GetFullPath(home)
        : IsPortable ? Path.Combine(AppContext.BaseDirectory, "Data")
        : null;

    /// <summary>
    /// Portable mode: an empty "portable.flag" next to VibeSalamander.exe keeps the settings, plugins, logs and
    /// caches in a "Data" folder beside it (a USB stick), nothing in the user's profile.
    /// </summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableFlag));

    /// <summary>Settings (settings.json) and the user's plugins.</summary>
    public static string Home => Override ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSalamander");

    /// <summary>The web view's own data (the viewer's previews, the editor): cache, not settings.</summary>
    public static string WebView => Override is { } home
        ? Path.Combine(home, "WebView2")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSalamander", "WebView2");

    /// <summary>The error logs.</summary>
    public static string Logs => Override is { } home
        ? Path.Combine(home, "Logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSalamander", "Logs");
}
