// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Diagnostics;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// MainViewModel: plugin viewers for F3 (IPluginViewer).
public sealed partial class MainViewModel
{
    /// <summary>Temporary text a plugin viewer gave the built-in viewer to show.</summary>
    private static string PluginViewFolder => Path.Combine(Path.GetTempPath(), "VibeSalamander", "PluginViews");

    /// <summary>The first plugin viewer that takes the file shows it; false = none did (the built-in viewer's turn).</summary>
    private async Task<bool> ViewWithPluginAsync(string path, bool onDisk)
    {
        if (!onDisk || !File.Exists(path))
            return false;
        string name = Path.GetFileName(path);
        foreach ((IPluginViewer viewer, PluginInfo plugin) in Plugins.Viewers.ToList())
        {
            try
            {
                if (!new WildcardMask(viewer.Masks).IsMatch(name) || !await Task.Run(() => viewer.CanView(path)))
                    continue;
                PluginView view = await viewer.ViewAsync(plugin.Host!, path);
                if (!view.Handled)
                    continue;
                if (view.TextContent is string text)
                {
                    string shown = await Task.Run(() => WriteViewText(text, view.Title ?? name));
                    _host.OpenViewer(shown, new[] { shown });
                }
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog.Exception($"Plugin viewer {plugin.Name}/{viewer.Id}", ex);
                Notify($"{plugin.Name}: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>The text in a file named after the title (the viewer's window shows the name).</summary>
    private static string WriteViewText(string text, string title)
    {
        string folder = Path.Combine(PluginViewFolder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string safe = string.Concat(title.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)).Trim();
        if (safe.Length == 0)
            safe = "View";
        if (!safe.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            safe += ".txt";
        string file = Path.Combine(folder, safe);
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(true));
        return file;
    }
}
