// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VibeSalamander.Plugins.MediaInfo;

/// <summary>
/// The Media Info window: each file's sections (General, Video, Audio #1, Text, Menu...) with their fields, as the
/// MediaInfo app's Tree view; Basic / Complete switches between the usual fields and all of them; Copy puts the text
/// report on the clipboard; Save Report writes it as text, HTML, XML or JSON.
/// </summary>
internal sealed class MediaInfoWindow
{
    private readonly MediaInfoPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly IReadOnlyList<string> _files;
    private readonly string _version;
    private IPluginListWindow? _window;
    private bool _complete;
    private string _text = string.Empty;

    public MediaInfoWindow(MediaInfoPlugin plugin, IPluginHost host, IReadOnlyList<string> files, string version)
    {
        _plugin = plugin;
        _host = host;
        _files = files;
        _version = version;
        _complete = plugin.Settings.Complete;
    }

    private string T(string text) => _plugin.T(text);

    public async Task ShowAsync()
    {
        string title = _files.Count == 1 ? Path.GetFileName(_files[0]) : string.Format(T("{0} files"), _files.Count);
        _window = _host.Ui.OpenListWindow(T("Media Info") + " - " + title, new[]
        {
            new PluginColumn(T("Field"), 260),
            new PluginColumn(T("Value"), 520),
        });
        _window.RowMenu = row => new[]
        {
            new PluginMenuItem(T("Copy Value"), null, () => _host.Ui.CopyToClipboard(row[1])),
            new PluginMenuItem(T("Copy Line"), null, () => _host.Ui.CopyToClipboard(row[0].Trim() + " : " + row[1])),
        };
        SetButtons();
        await FillAsync();
    }

    private void SetButtons() => _window?.SetButtons(new[]
    {
        new PluginButton("complete", T(_complete ? "Basic" : "Complete"), () => _ = SwitchAsync()),
        new PluginButton("copy", T("Copy"), () => _host.Ui.CopyToClipboard(_text)),
        new PluginButton("save", T("Save Report..."), () => _ = SaveAsync()),
    });

    private async Task SwitchAsync()
    {
        _complete = !_complete;
        SetButtons();
        await FillAsync();
    }

    /// <summary>Reads the files (off the UI thread) and lists their sections and fields.</summary>
    private async Task FillAsync()
    {
        IPluginListWindow window = _window!;
        foreach (IPluginRow row in window.Rows.ToList())
            row.Remove();
        window.SetProgress(null);
        window.SetStatus(T("Reading..."));
        bool complete = _complete;
        List<string?> reports = await Task.Run(() => _files.Select(f => MediaReport.Inform(f, complete)).ToList());
        if (window.IsClosed || complete != _complete)
            return; // closed, or switched again meanwhile (that fill shows)
        _text = MediaInfoPlugin.Join(_files, reports, "Text");
        int fields = 0;
        for (int i = 0; i < _files.Count; i++)
        {
            string file = _files[i];
            string? report = reports[i];
            if (_files.Count > 1)
                window.AddRow(new[] { "▍ " + Path.GetFileName(file), string.Empty });
            if (report == null)
            {
                window.AddRow(new[] { "    " + T("(MediaInfo can't read this file)"), string.Empty });
                continue;
            }
            foreach (MediaSection section in MediaReport.Sections(report))
            {
                window.AddRow(new[] { section.Title, string.Empty });
                foreach ((string field, string value) in section.Fields)
                {
                    window.AddRow(new[] { "    " + field, value });
                    fields++;
                }
            }
        }
        window.SetStatus(string.Format(T("{0} fields ({1} view) · MediaInfo {2}"), fields, T(_complete ? "Complete" : "Basic"), _version));
    }

    private async Task SaveAsync()
    {
        if (_window == null)
            return;
        var types = MediaReport.Formats.Select(f => new PluginFileType(f.Name, f.Extension)).ToList();
        string folder = Path.GetDirectoryName(_files[0]) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string name = _files.Count == 1 ? Path.GetFileNameWithoutExtension(_files[0]) + " - MediaInfo" : "MediaInfo";
        PluginSaveChoice? choice = await _host.Ui.AskSaveFileAsync(T("Save Report"), Path.Combine(folder, name + ".txt"), types, 0, _window);
        if (choice == null)
            return;
        (string _, string extension, string output) = MediaReport.Formats[Math.Clamp(choice.TypeIndex, 0, MediaReport.Formats.Length - 1)];
        string path = Path.GetExtension(choice.Path).Length == 0 ? choice.Path + extension : choice.Path;
        if (File.Exists(path) &&
            !await _host.Ui.ConfirmAsync(T("Save Report"), string.Format(T("The file '{0}' already exists. Do you wish to overwrite it?"), path), _window))
            return;
        try
        {
            string report = await MediaInfoPlugin.ReportAsync(_files, _complete, output);
            await File.WriteAllTextAsync(path, report, new UTF8Encoding(false));
            _window.SetStatus(string.Format(T("Saved to {0}"), path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _host.Ui.ShowMessageAsync(T("Save Report"), ex.Message, _window);
        }
    }
}
