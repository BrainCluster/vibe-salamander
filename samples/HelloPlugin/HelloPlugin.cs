// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Plugins;

namespace HelloPlugin;

/// <summary>
/// The smallest useful plugin: three commands in Plugins → Hello. One reads the active panel and shows a
/// notification, one opens a window with a list, and one appears in the right-click menu of text files.
/// It also has a settings page and a viewer: F3 on a .csv file shows its columns lined up.
/// </summary>
public sealed class HelloPlugin : IVibePlugin, ICommandContributor
{
    // ---- who the plugin is (the Plugins Manager shows these) -----------------------------------------------

    public string Id => "example.hello";
    public string DisplayName => "Hello";
    public Version Version => new(1, 0);
    public string Description => "An example plugin: counts marked files, lists a folder's largest files, measures text files.";
    public string Author => "Your name";
    public string? IconGlyph => "\uE8BD"; // a Segoe Fluent Icons character for the Plugins menu

    // the oldest Vibe Salamander it works with; null = any. Say the version that added the newest thing you use
    // (settings pages and RegisterViewer came after 0.9.0), and an older Vibe Salamander explains instead of failing.
    public Version? MinimumHostVersion => null;

    // ---- its settings page (Configuration → Plugins → Settings...) -----------------------------------------

    private int _largestCount = 50;
    private string _settingsFile = string.Empty;

    public IReadOnlyList<PluginField>? GetSettings() => new[]
    {
        new PluginField { Label = "How many files Largest files lists", Value = _largestCount.ToString(CultureInfo.InvariantCulture) },
    };

    public void ApplySettings(IReadOnlyList<PluginField> fields)
    {
        if (int.TryParse(fields[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0)
            _largestCount = Math.Min(count, 10_000);
        File.WriteAllText(_settingsFile, _largestCount.ToString(CultureInfo.InvariantCulture)); // kept in the plugin's own folder
    }

    // ---- what it adds ----------------------------------------------------------------------------------------

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    public void Initialize(IPluginHost host)
    {
        _settingsFile = Path.Combine(host.GetDataDirectory(this), "settings.txt");
        if (File.Exists(_settingsFile) && int.TryParse(File.ReadAllText(_settingsFile), out int saved) && saved > 0)
            _largestCount = saved;
        Commands = new[]
        {
            new PluginCommand("count", "Count marked files", null, CountMarked),
            new PluginCommand("largest", "Largest files here...", null, ListLargest),
            // AppliesTo: the command also shows in the right-click menu of files it fits (under "Hello")
            new PluginCommand("lines", "Count lines", null, CountLines,
                AppliesTo: path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), SeparatorBefore: true),
        };
        host.RegisterCommands(this);
        host.RegisterViewer(new CsvViewer());
    }

    public void Shutdown()
    {
    }

    // ---- the commands (they run on the UI thread: long work goes to Task.Run) -------------------------------

    private static Task CountMarked(IPluginHost host)
    {
        IPanelContext panel = host.ActivePanel;
        int files = panel.MarkedItems.Count(i => !i.IsDirectory);
        int folders = panel.MarkedItems.Count(i => i.IsDirectory);
        // a short message in the notification bar: no dialog to close
        host.Notify($"{files} files and {folders} folders marked in {panel.Path}");
        return Task.CompletedTask;
    }

    private async Task ListLargest(IPluginHost host)
    {
        string folder = host.ActivePanel.Path;
        if (!Directory.Exists(folder))
        {
            host.Notify("Open a folder on a disk first.", isError: true);
            return;
        }
        // a window with columns, a status line, a progress bar and buttons
        IPluginListWindow window = host.Ui.OpenListWindow("Largest files", new[]
        {
            new PluginColumn("Name", 0),
            new PluginColumn("Size", 140, AlignRight: true),
        });
        window.SetButtons(new[] { new PluginButton("close", "Close", window.Close, IsDefault: true) });
        window.SetProgress(-1); // busy, end unknown
        List<FileInfo> largest = await Task.Run(() => new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderByDescending(f => f.Length).Take(_largestCount).ToList());
        foreach (FileInfo file in largest)
            window.AddRow(new[] { Path.GetRelativePath(folder, file.FullName), file.Length.ToString("N0", CultureInfo.CurrentCulture) });
        window.SetProgress(null);
        window.SetStatus($"The {largest.Count} largest files in {folder}");
        // a right-click menu for the rows
        window.RowMenu = row => new[] { new PluginMenuItem("Copy name", null, () => host.Ui.CopyToClipboard(row[0])) };
    }

    private static async Task CountLines(IPluginHost host)
    {
        if (host.ActivePanel.FocusedItem is not FileEntry { IsDirectory: false } file)
            return;
        int lines = await Task.Run(() => File.ReadLines(file.FullPath).Count());
        await host.Ui.ShowMessageAsync("Count lines", $"{file.Name} has {lines:N0} lines.");
    }
}

/// <summary>F3 on a .csv file: its columns lined up, in the built-in viewer (search, copy and print come with it).</summary>
public sealed class CsvViewer : IPluginViewer
{
    public string Id => "csv";
    public string DisplayName => "CSV tables";
    public string Masks => "*.csv";

    public async Task<PluginView> ViewAsync(IPluginHost host, string path)
    {
        string[] lines = await File.ReadAllLinesAsync(path);
        if (lines.Length == 0)
            return PluginView.NotHandled; // nothing to line up: the built-in viewer shows the empty file
        char separator = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        List<string[]> rows = lines.Select(l => l.Split(separator)).ToList();
        int columns = rows.Max(r => r.Length);
        int[] widths = Enumerable.Range(0, columns).Select(c => rows.Max(r => c < r.Length ? r[c].Trim().Length : 0)).ToArray();
        var text = new System.Text.StringBuilder();
        foreach (string[] row in rows)
            text.AppendLine(string.Join(" | ", Enumerable.Range(0, columns).Select(c => (c < row.Length ? row[c].Trim() : string.Empty).PadRight(widths[c]))).TrimEnd());
        return PluginView.Text(text.ToString(), Path.GetFileName(path) + " (columns)");
    }
}
