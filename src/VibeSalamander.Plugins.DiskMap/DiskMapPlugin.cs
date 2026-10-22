// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>
/// The built-in Disk Map plugin (Open Salamander's DiskMap, modernised): what takes the space in a directory,
/// as Blocks (the default), a Sunburst, Columns, or the original's cushion treemap (Classic).
/// Commands → Disk Map maps the active panel's directory.
/// </summary>
public sealed class DiskMapPlugin : IVibePlugin, ICommandContributor
{
    public string Id => "vibe.diskmap";
    public string DisplayName => "Disk Map";
    public Version Version => new(1, 0);
    public string Description => "Shows what takes the space in a directory: a cushion treemap like Salamander's Disk Map, nested blocks, a sunburst or columns.";
    public string Author => "Vibe Salamander";
    public string? IconGlyph => "\uEB05";

    private IPluginHost? _host;
    private string? _settingsFile;
    private string _lastView = string.Empty;

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    /// <summary>The (English) name of the view chosen last: the next window opens with it.</summary>
    internal string LastView
    {
        get => _lastView;
        set
        {
            if (_lastView == value)
                return;
            _lastView = value;
            try
            {
                if (_settingsFile != null)
                    File.WriteAllText(_settingsFile, value);
            }
            catch (Exception)
            {
                // only a convenience
            }
        }
    }

    public void Initialize(IPluginHost host)
    {
        _host = host;
        try
        {
            _settingsFile = Path.Combine(host.GetDataDirectory(this), "view name.txt");
            if (File.Exists(_settingsFile))
                _lastView = File.ReadAllText(_settingsFile).Trim();
        }
        catch (Exception)
        {
            _settingsFile = null;
        }
        Commands = new[]
        {
            new PluginCommand("map", "Show disk map", null, h =>
            {
                Open(h);
                return Task.CompletedTask;
            }, HostCommand: "DiskMap"),
        };
        host.RegisterCommands(this);
    }

    public void Shutdown()
    {
    }

    private void Open(IPluginHost host)
    {
        string? path = PathToMap(host.ActivePanel);
        if (path == null)
        {
            host.Notify(host.Ui.T("Disk Map shows directories on disks and network shares: open one in the panel (or focus a drive in This PC)."), isError: true);
            return;
        }
        _ = new MapSession(host, this, path);
    }

    /// <summary>The panel's directory; in This PC the focused drive.</summary>
    internal static string? PathToMap(IPanelContext panel)
    {
        static bool IsDiskPath(string p) =>
            p.Length >= 2 && (p[1] == ':' || p.StartsWith(@"\\", StringComparison.Ordinal)) && !p.Contains("://", StringComparison.Ordinal);
        string path = panel.Path;
        if (IsDiskPath(path) && Directory.Exists(path))
            return path;
        if (panel.FocusedItem is { IsDirectory: true } focused && IsDiskPath(focused.FullPath) && Directory.Exists(focused.FullPath))
            return focused.FullPath;
        return null;
    }
}
