using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Settings;

public enum Density
{
    /// <summary>Salamander-like: 20 px rows, small paddings.</summary>
    Compact,
    /// <summary>Files-like: 28 px rows, more breathing room.</summary>
    Comfortable,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class PanelSettings
{
    public string Path { get; set; } = "C:\\";
    public SortMode SortMode { get; set; } = SortMode.Name;
    public bool SortDescending { get; set; }
    public ViewMode ViewMode { get; set; } = ViewMode.Detailed;
    public ColumnSettings Columns { get; set; } = new();
}

/// <summary>User configuration, stored as JSON in %AppData%\VibeSalamander\settings.json.</summary>
public sealed class AppSettings
{
    public PanelSettings Left { get; set; } = new();
    public PanelSettings Right { get; set; } = new();
    public bool RightPanelActive { get; set; }

    public bool ShowHidden { get; set; }
    public Density Density { get; set; } = Density.Compact;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool AlwaysOnTop { get; set; }

    /// <summary>Hot paths 0-9: Ctrl+Shift+digit stores the panel's path, Ctrl+digit goes there ("" = not set).</summary>
    public List<string> HotPaths { get; set; } = Enumerable.Repeat(string.Empty, 10).ToList();

    // bars (Options → Show)
    public bool ShowTopToolbar { get; set; } = true;
    public bool ShowDriveBars { get; set; } = true;
    public bool ShowCommandLine { get; set; } = true;
    public bool ShowFunctionKeyBar { get; set; } = true;
    public bool ShowHeaderLine { get; set; } = true;
    public bool ShowInformationLine { get; set; } = true;

    /// <summary>
    /// F5/F6 ask for the target. When switched off ("Don't show this dialog again"), files go straight
    /// into the other panel's directory. Options → Ask for Copy/Move Target turns it back on.
    /// </summary>
    public bool AskCopyMoveTarget { get; set; } = true;

    /// <summary>Ask before moving to the Recycle Bin (F8/Del). Off by default: it can be undone, like in Explorer.</summary>
    public bool ConfirmRecycle { get; set; }

    /// <summary>Transfer panel below the file panels (shown while it has items).</summary>
    public bool ShowTransfers { get; set; } = true;

    /// <summary>Height of the transfer panel in DIPs (resized with the splitter above it).</summary>
    public double TransfersHeight { get; set; } = 150;

    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1400;
    public int WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    /// <summary>Left panel width as a fraction of the panel area (0.5 = equal).</summary>
    public double SplitRatio { get; set; } = 0.5;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSalamander");

    public static string FilePath => System.IO.Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // corrupt or locked settings: start with defaults
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are best effort
        }
    }
}
