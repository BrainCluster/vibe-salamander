// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace VibeSalamander.Plugins.Checksum;

/// <summary>
/// The built-in Checksum plugin (Open Salamander's Checksum plugin): calculates CRC32 (SFV), MD5, SHA-1,
/// SHA-256 and SHA-512 checksums of files and directories and saves them to a checksum file, and verifies
/// the files listed in an SFV / MD5 / SHA-1 / SHA-256 / SHA-512 file (Ctrl+Shift+V).
/// </summary>
public sealed class ChecksumPlugin : IVibePlugin, ICommandContributor
{
    public string Id => "vibe.checksum";
    public string DisplayName => "Checksum";
    public Version Version => new(1, 0);
    public string Description => "SFV, MD5, SHA-1, SHA-256 and SHA-512 checksum calculator and verifier.";
    public string Author => "Vibe Salamander";

    private IPluginHost? _host;
    private ChecksumSettings _settings = new();
    private string? _settingsFile;

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settingsFile = Path.Combine(host.GetDataDirectory(this), "settings.json");
        _settings = ChecksumSettings.Load(_settingsFile);
        Commands = new[]
        {
            new PluginCommand("calculate", "Calculate Checksums...", null, Calculate, HostCommand: "CalculateChecksums"),
            new PluginCommand("verify", "Verify Checksums...", "Ctrl+Shift+V", Verify, HostCommand: "VerifyChecksums"),
            new PluginCommand("configure", "Configuration...", null, h => ConfigureAsync(h, null)),
        };
        host.RegisterCommands(this);
    }

    public void Shutdown()
    {
    }

    internal void SaveSettings()
    {
        if (_settingsFile != null)
            _settings.Save(_settingsFile);
    }

    private Task Calculate(IPluginHost host)
    {
        IPanelContext panel = host.ActivePanel;
        var items = panel.MarkedItems.Count > 0 ? panel.MarkedItems
            : panel.FocusedItem is { IsUpDir: false, Tag: null } focused ? new[] { focused } : Array.Empty<Core.FileSystem.FileEntry>();
        if (items.Count == 0 || !Directory.Exists(panel.Path))
        {
            host.Notify(host.Ui.T("Select the files and directories to calculate checksums for."), isError: true);
            return Task.CompletedTask;
        }
        new CalculateSession(this, host, _settings, panel.Path, items).Start();
        return Task.CompletedTask;
    }

    private async Task Verify(IPluginHost host)
    {
        var focused = host.ActivePanel.FocusedItem;
        if (focused == null || focused.IsDirectory || focused.IsUpDir || !File.Exists(focused.FullPath))
        {
            host.Notify(host.Ui.T("Focus the SFV, MD5, SHA-1, SHA-256 or SHA-512 file to verify."), isError: true);
            return;
        }
        await new VerifySession(host, focused.FullPath).StartAsync();
    }

    /// <summary>Which checksums are calculated (Open Salamander's Checksum Configuration).</summary>
    internal async Task ConfigureAsync(IPluginHost host, IPluginListWindow? owner)
    {
        var options = HashKinds.All.Select(k => new PluginOption
        {
            Label = k == HashKind.Crc ? "CRC/SFV" : HashKinds.ColumnName(k),
            Value = _settings.Calculate.Contains(k),
        }).ToList();
        if (!await host.Ui.EditOptionsAsync(host.Ui.T("Checksum Configuration"),
                host.Ui.T("Calculate these checksums (fewer are faster):"), options, owner))
            return;
        var chosen = HashKinds.All.Where((k, i) => options[i].Value).ToList();
        _settings.Calculate = chosen.Count > 0 ? chosen : new List<HashKind> { HashKind.Crc };
        SaveSettings();
        if (owner != null)
            await host.Ui.ShowMessageAsync(host.Ui.T("Checksum Configuration"),
                host.Ui.T("The changes will take effect when the 'Calculate Checksums' window is opened next time."), owner);
    }
}

/// <summary>The plugin's settings (in its data folder).</summary>
internal sealed class ChecksumSettings
{
    public List<HashKind> Calculate { get; set; } = new(HashKinds.All);

    /// <summary>The format chosen the last time checksums were saved.</summary>
    public HashKind LastSaveKind { get; set; } = HashKind.Crc;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ChecksumSettings Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<ChecksumSettings>(File.ReadAllText(file), Json) ?? new ChecksumSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // broken settings: the defaults
        }
        return new ChecksumSettings();
    }

    public void Save(string file)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not saved: the defaults next time
        }
    }
}
