using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Tools;

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

    /// <summary>Hot paths 1-9 (index 0 is unused): Ctrl+Shift+digit stores the panel's path, Ctrl+digit goes there ("" = not set).</summary>
    public List<string> HotPaths { get; set; } = Enumerable.Repeat(string.Empty, 10).ToList();

    /// <summary>User menu (F9), edited in Commands → User Menu → Edit.</summary>
    public List<UserMenuItem> UserMenu { get; set; } = Tools.UserMenu.CreateDefaults();

    /// <summary>Last options of Compare Directories (Ctrl+F10).</summary>
    public CompareOptions Compare { get; set; } = new();

    /// <summary>List of Opened Files (Alt+F11), most recent first.</summary>
    public List<string> OpenedFiles { get; set; } = new();

    /// <summary>List of Working Directories (Alt+F12), most recent first.</summary>
    public List<string> WorkingDirectories { get; set; } = new();

    // ---- Configuration pages (as in Open Salamander) ----------------------------------------

    public GeneralOptions General { get; set; } = new();
    public RecycleOptions Recycle { get; set; } = new();
    public ConfirmationOptions Confirm { get; set; } = new();
    public PanelOptions Panels { get; set; } = new();
    public AppearanceOptions Appearance { get; set; } = new();
    public ColorOptions Colors { get; set; } = new();
    public MainWindowOptions Window { get; set; } = new();
    public DriveOptions Drives { get; set; } = new();
    public HistoryOptions History { get; set; } = new();
    public PluginOptions Plugins { get; set; } = new();

    /// <summary>Viewers (F3) by file mask, first match wins; no match = the default Windows app.</summary>
    public List<ProgramRule> Viewers { get; set; } = new();

    /// <summary>Editors (F4) by file mask, first match wins; no match = the Windows "edit" verb.</summary>
    public List<ProgramRule> Editors { get; set; } = new();

    /// <summary>Command line history (Up / Down), most recent first.</summary>
    public List<string> CommandLineHistory { get; set; } = new();

    /// <summary>Names of the hot paths (shown in menus and on the hot path bar; "" = the directory name).</summary>
    public List<string> HotPathNames { get; set; } = Enumerable.Repeat(string.Empty, 10).ToList();

    /// <summary>Changes to the default keyboard shortcuts (Configuration → Keyboard).</summary>
    public List<KeyBindingOverride> KeyBindings { get; set; } = new();

    /// <summary>Top toolbar items: command names, "-" separators and "ViewMenu" (Configuration → Toolbar).</summary>
    public List<string> Toolbar { get; set; } = CommandCatalog.DefaultToolbar();

    /// <summary>Hot path bar and user menu bar below the top toolbar (Options → Show).</summary>
    public bool ShowHotPathBar { get; set; }
    public bool ShowUserMenuBar { get; set; }

    // bars (Options → Show)
    public bool ShowTopToolbar { get; set; } = true;
    public bool ShowDriveBars { get; set; } = true;
    public bool ShowCommandLine { get; set; } = true;
    public bool ShowFunctionKeyBar { get; set; } = true;
    public bool ShowHeaderLine { get; set; } = true;
    public bool ShowInformationLine { get; set; } = true;

    /// <summary>
    /// F5/F6 ask for the target. When switched off ("Don't show this dialog again"), files go straight
    /// into the other panel's directory. Configuration → Confirmations turns it back on.
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

    /// <summary>Export Configuration: writes the settings to a file of the user's choice.</summary>
    public static void Export(AppSettings settings, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));

    /// <summary>Import Configuration: reads and validates a settings file (throws when it isn't one).</summary>
    public static AppSettings ReadFile(string path)
    {
        string json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new InvalidDataException("The file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This is not a Vibe Salamander configuration file: " + ex.Message, ex);
        }
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
