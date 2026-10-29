// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <summary>Widths of the Find window's Name, Size and Modified columns (Path takes the rest).</summary>
public sealed class FindColumnWidths
{
    public const double DefaultName = 260;
    public const double DefaultSize = 110;
    public const double DefaultDate = 140;

    public double Name { get; set; } = DefaultName;
    public double Size { get; set; } = DefaultSize;
    public double Date { get; set; } = DefaultDate;
}

/// <summary>One text row of a saved search.</summary>
public sealed class SavedText
{
    public string Text { get; set; } = string.Empty;

    /// <summary>"Text", "Regex" or "Hex".</summary>
    public string Mode { get; set; } = "Text";

    /// <summary>"Doesn't contain".</summary>
    public bool Negate { get; set; }
}

/// <summary>Everything the Find window asks, as typed (also the last search, and searches saved by name).</summary>
public sealed class SavedSearch
{
    public string Name { get; set; } = string.Empty;
    public string Named { get; set; } = "*.*";

    /// <summary>Empty: the active panel's folder.</summary>
    public string LookIn { get; set; } = string.Empty;

    public List<SavedText> Texts { get; set; } = new();
    public bool AllTexts { get; set; } = true;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool Subdirectories { get; set; } = true;
    public bool Directories { get; set; } = true;
    public bool Hidden { get; set; } = true;
    public bool InsideArchives { get; set; }

    /// <summary>Sizes as typed ("10 MB"); empty = no limit.</summary>
    public string MinSize { get; set; } = string.Empty;
    public string MaxSize { get; set; } = string.Empty;

    /// <summary>"Any", "Today", "Week", "Month", "Year" or "Between".</summary>
    public string Modified { get; set; } = "Any";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>"R+H-": attributes that must be there (+) or not (−); others don't matter.</summary>
    public string Attributes { get; set; } = string.Empty;

    /// <summary>Levels below the folder (null = all).</summary>
    public int? Depth { get; set; }

    public string SkipFolders { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    /// <summary>The settings file couldn't be read at start (locked): these settings aren't saved over it.</summary>
    [JsonIgnore]
    public bool DoNotSave { get; set; }

    /// <summary>There was no settings file: the first start (Help → First Steps is suggested).</summary>
    [JsonIgnore]
    public bool IsNew { get; set; }

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

    /// <summary>Column widths of the Find window's results (dragged at the column headers).</summary>
    public FindColumnWidths FindColumns { get; set; } = new();

    /// <summary>The Find window's last search (restored when it opens).</summary>
    public SavedSearch LastSearch { get; set; } = new();

    /// <summary>Searches saved by name in the Find window.</summary>
    public List<SavedSearch> SavedSearches { get; set; } = new();

    /// <summary>Find Duplicates: "Content", "Name", "Size", "Name, Size" (the last choice).</summary>
    public string DuplicatesBy { get; set; } = "Content";

    /// <summary>The multi-rename tool's last masks, search and counter (Ctrl+M in Total Commander's shortcuts).</summary>
    public Tools.MultiRenameOptions MultiRename { get; set; } = new();

    public WindowPlacement MultiRenameWindow { get; set; } = new();

    /// <summary>Classic: one view per panel; Modern: views per folder, like Explorer (Configuration → Views).</summary>
    public ViewStyle ViewStyle { get; set; } = ViewStyle.Modern;

    /// <summary>Modern views: the view chosen in each folder, by <see cref="Panels.FolderViews.Key"/>.</summary>
    public Dictionary<string, FolderView> FolderViews { get; set; } = new();

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

    /// <summary>The built-in viewer (F3) and editor (F4).</summary>
    public ViewerOptions Viewer { get; set; } = new();

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

    /// <summary>Whose shortcuts the key map starts from: Salamander's or Total Commander's (the changes above apply on top).</summary>
    public KeyScheme KeyScheme { get; set; } = KeyScheme.Salamander;

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

    /// <summary>The transfer panel stays visible even with no transfers (otherwise only while it has transfers).</summary>
    public bool AlwaysShowTransfers { get; set; }

    /// <summary>Seconds after the last transfer finished until the transfer panel closes (0 = it stays open).</summary>
    public int TransfersAutoCloseSeconds { get; set; } = 2;

    /// <summary>A speed limit for all transfers together, in bytes per second (0 = none).</summary>
    public long TransfersSpeedLimit { get; set; }

    /// <summary>Each transfer shows a graph of its speed.</summary>
    public bool TransfersGraphs { get; set; }

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
        AppPaths.Home;

    public static string FilePath => System.IO.Path.Combine(Directory, "settings.json");

    public static AppSettings Load() => Load(FilePath, out _);

    /// <summary>
    /// Reads the settings, as much of them as possible: a setting that can't be read (a value from a newer version,
    /// a typing mistake in the file) goes back to its default and the rest is kept; a missing part (null) gets its
    /// default. A file that can't be read at all gives the defaults. Either way the old file is kept beside it
    /// (settings.broken-….json) and <paramref name="problem"/> says what happened (for the error log).
    /// A file that is locked is read again a few times; if it stays locked, these settings aren't saved over it.
    /// </summary>
    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        string json;
        try
        {
            if (!File.Exists(path))
                return new AppSettings { IsNew = true };
            json = ReadWithRetries(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The settings file couldn't be read ({ex.Message}). The program runs with the default settings and doesn't save over the file.";
            return new AppSettings { DoNotSave = true };
        }

        var repairs = new List<string>();
        AppSettings? settings = null;
        try
        {
            JsonNode? root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            for (int attempt = 0; attempt < 100 && root is JsonObject && settings == null; attempt++)
            {
                try
                {
                    settings = root.Deserialize<AppSettings>(Options);
                }
                catch (JsonException ex) when (ex.Path is { Length: > 1 } bad && RemoveAt(root, bad))
                {
                    repairs.Add(bad.TrimStart('$', '.'));
                }
            }
        }
        catch (Exception)
        {
            settings = null; // whatever it is, the program starts (with the defaults)
        }

        if (settings == null)
        {
            string? kept = KeepBrokenFile(path);
            problem = "The settings file couldn't be read: the program started with the default settings." +
                      (kept != null ? $" The old file is kept as {Path.GetFileName(kept)}." : string.Empty);
            return new AppSettings();
        }
        List<string> filled = Normalize(settings);
        if (repairs.Count > 0)
        {
            string? kept = KeepBrokenFile(path);
            problem = "Some settings couldn't be read and are back to their defaults: " + string.Join(", ", repairs) + "." +
                      (kept != null ? $" The old file is kept as {Path.GetFileName(kept)}." : string.Empty);
        }
        else if (filled.Count > 0)
        {
            problem = "Some settings were missing and got their defaults: " + string.Join(", ", filled) + ".";
        }
        return settings;
    }

    private static string ReadWithRetries(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(150); // an antivirus or backup program reading it right now
            }
        }
    }

    /// <summary>Removes the value at a System.Text.Json path ("$.Left.ViewMode", "$.HotPaths[3]", "$.FolderViews['C:\\X']").</summary>
    private static bool RemoveAt(JsonNode root, string path)
    {
        var steps = new List<object>(); // string = property, int = array index
        int i = path.StartsWith('$') ? 1 : 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                    end++;
                steps.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                steps.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), out int index))
                    return false;
                steps.Add(index);
                i = end + 1;
            }
            else
            {
                return false;
            }
        }
        if (steps.Count == 0)
            return false;
        JsonNode? node = root;
        for (int s = 0; s < steps.Count - 1 && node != null; s++)
            node = steps[s] is int n ? (node is JsonArray a && n < a.Count ? a[n] : null) : (node as JsonObject)?[(string)steps[s]];
        switch (steps[^1])
        {
            case string name when node is JsonObject obj && obj.ContainsKey(name):
                return obj.Remove(name);
            case int index when node is JsonArray array && index < array.Count:
                array.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A copy of a settings file that couldn't be read (once per content); its path, or null.</summary>
    private static string? KeepBrokenFile(string path)
    {
        try
        {
            byte[] content = File.ReadAllBytes(path);
            string folder = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            foreach (string earlier in System.IO.Directory.EnumerateFiles(folder, name + ".broken-*.json"))
                if (File.ReadAllBytes(earlier).AsSpan().SequenceEqual(content))
                    return earlier;
            string copy = Path.Combine(folder, $"{name}.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllBytes(copy, content);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Settings that are null but mustn't be (a "null" in the file, a part an older version didn't have) get their
    /// default (the value a new settings object has); null entries of text lists become "". Returns what was filled in.
    /// </summary>
    public static List<string> Normalize(AppSettings settings)
    {
        var filled = new List<string>();
        Fill(settings, string.Empty, filled, 0);
        // the hot paths are numbered 0-9
        while (settings.HotPaths.Count < 10)
            settings.HotPaths.Add(string.Empty);
        return filled;
    }

    private static readonly NullabilityInfoContext Nullability = new();

    private static void Fill(object target, string where, List<string> filled, int depth)
    {
        if (depth > 6)
            return;
        Type type = target.GetType();
        object? defaults = null;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType ||
                property.IsDefined(typeof(JsonIgnoreAttribute)))
                continue;
            object? value = property.GetValue(target);
            if (value == null)
            {
                if (Nullability.Create(property).WriteState != NullabilityState.NotNull)
                    continue; // null is allowed here
                defaults ??= Activator.CreateInstance(type);
                value = property.PropertyType == typeof(string) ? string.Empty : property.GetValue(defaults!);
                if (value == null)
                    continue;
                property.SetValue(target, value);
                filled.Add(where + property.Name);
            }
            if (value is IList<string> texts)
            {
                for (int i = 0; i < texts.Count; i++)
                    texts[i] ??= string.Empty;
            }
            else if (value is IList list)
            {
                foreach (object? item in list)
                    if (item != null && IsSettingsType(item.GetType()))
                        Fill(item, where + property.Name + ".", filled, depth + 1);
            }
            else if (IsSettingsType(value.GetType()))
            {
                Fill(value, where + property.Name + ".", filled, depth + 1);
            }
        }
    }

    private static bool IsSettingsType(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("VibeSalamander.Core", StringComparison.Ordinal) == true &&
        type.GetConstructor(Type.EmptyTypes) != null;

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

    public static void Save(AppSettings settings) => Save(settings, FilePath);

    /// <summary>Writes the settings whole or not at all (a new file, then swapped in).</summary>
    public static void Save(AppSettings settings, string path)
    {
        if (settings.DoNotSave)
            return; // the file couldn't be read at start: these defaults mustn't replace it
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are best effort
        }
    }
}
