// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Plugins;

namespace HelloPlugin;

/// <summary>
/// The smallest useful plugin: three commands in Plugins → Hello. One reads the active panel and shows a
/// notification, one opens a window with a list, and one appears in the right-click menu of text files.
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

    // ---- what it adds ----------------------------------------------------------------------------------------

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    public void Initialize(IPluginHost host)
    {
        Commands = new[]
        {
            new PluginCommand("count", "Count marked files", null, CountMarked),
            new PluginCommand("largest", "Largest files here...", null, ListLargest),
            // AppliesTo: the command also shows in the right-click menu of files it fits (under "Hello")
            new PluginCommand("lines", "Count lines", null, CountLines,
                AppliesTo: path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), SeparatorBefore: true),
        };
        host.RegisterCommands(this);
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

    private static async Task ListLargest(IPluginHost host)
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
            .OrderByDescending(f => f.Length).Take(50).ToList());
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
