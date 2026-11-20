// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Plugins.MediaInfo;

/// <summary>
/// The Media Info plugin (a separate download, like 7-Zip): what the MediaInfo app shows about video, audio and image files - container,
/// codecs, profiles, bit rates, resolution, frame rate, HDR, channels, sampling, languages, chapters and tags - read
/// by MediaInfo's own library. Plugins → Media Info (and the right-click menu of media files) opens a window with
/// the report of the focused or marked files, which can be copied or saved as text, HTML, XML or JSON; F3 on a video
/// or audio file shows the report in the viewer.
/// </summary>
public sealed class MediaInfoPlugin : IVibePlugin, ICommandContributor
{
    // viewers and settings pages came in 0.9.1 (its right-click entry shows from 0.9.2 on, but nothing fails before)
    public Version? MinimumHostVersion { get; } = new(0, 9, 1);

    public string Id => "vibe.mediainfo";
    public string DisplayName => "Media Info";
    public Version Version => new(1, 0);
    public string Description => "Technical and tag details of video, audio and image files, read by MediaInfo's library.";
    public string Author => "Vibe Salamander (MediaInfoLib by MediaArea.net)";
    public string? IconGlyph => ""; // a film strip

    private IPluginHost? _host;
    private MediaInfoSettings _settings = new();
    private string? _settingsFile;

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    internal string T(string text) => _host?.Ui.T(text) ?? text;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settingsFile = Path.Combine(host.GetDataDirectory(this), "settings.json");
        _settings = MediaInfoSettings.Load(_settingsFile);
        Commands = new[]
        {
            new PluginCommand("show", "Media Info...", null, ShowAsync, AppliesTo: IsMediaFile),
        };
        host.RegisterCommands(this);
        host.RegisterViewer(new MediaInfoViewer(this));
    }

    public void Shutdown()
    {
    }

    internal MediaInfoSettings Settings => _settings;

    // ---- settings page (Configuration → Plugins → Media Info → Settings...) ------------------------------------

    public IReadOnlyList<PluginField>? GetSettings() => new[]
    {
        new PluginField { Label = T("Show every field MediaInfo knows (its Complete view)"), Kind = PluginFieldKind.Check, Value = _settings.Complete ? "true" : "false" },
        new PluginField { Label = T("F3 on a video or audio file shows its media information"), Kind = PluginFieldKind.Check, Value = _settings.Viewer ? "true" : "false" },
    };

    public void ApplySettings(IReadOnlyList<PluginField> fields)
    {
        _settings.Complete = fields[0].Value == "true";
        _settings.Viewer = fields[1].Value == "true";
        if (_settingsFile != null)
            _settings.Save(_settingsFile);
    }

    // ---- the window ----------------------------------------------------------------------------------------

    /// <summary>The right-click menu offers it for video, audio and picture files.</summary>
    private static bool IsMediaFile(string path)
    {
        string name = Path.GetFileName(path);
        return new WildcardMask(MediaReport.ViewerMasks + ";" + PictureMasks).IsMatch(name);
    }

    private const string PictureMasks = "*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.jxl;*.dds;*.exr;*.psd";

    private async Task ShowAsync(IPluginHost host)
    {
        IPanelContext panel = host.ActivePanel;
        IReadOnlyList<FileEntry> marked = panel.MarkedItems;
        var files = (marked.Count > 0 ? marked : panel.FocusedItem is { } focused ? new[] { focused } : Array.Empty<FileEntry>())
            .Where(f => !f.IsDirectory && !f.IsUpDir && f.Tag == null && File.Exists(f.FullPath))
            .Select(f => f.FullPath).ToList();
        if (files.Count == 0)
        {
            host.Notify(T("Focus or mark the video, audio or picture files to show their media information."), isError: true);
            return;
        }
        if (MediaReport.LibraryVersion() is not string version)
        {
            host.Notify(T("MediaInfo's library is missing: reinstall Vibe Salamander."), isError: true);
            return;
        }
        await new MediaInfoWindow(this, host, files, version).ShowAsync();
    }

    /// <summary>The report of the files in one of MediaReport.Formats' outputs (read off the UI thread).</summary>
    internal static Task<string> ReportAsync(IReadOnlyList<string> files, bool complete, string output) =>
        Task.Run(() => Join(files, files.Select(f => MediaReport.Inform(f, complete, output)).ToList(), output));

    /// <summary>
    /// Several files' reports as one: text and HTML one after another (text under each file's name), JSON as an
    /// array, XML inside one root element, so the saved file stays valid.
    /// </summary>
    internal static string Join(IReadOnlyList<string> files, IReadOnlyList<string?> reports, string output)
    {
        if (files.Count == 1)
            return reports[0] ?? (output == "Text" ? "(MediaInfo can't read this file)\n" : string.Empty);
        var text = new StringBuilder();
        var present = reports.Where(r => r != null).Select(r => r!.Trim()).ToList();
        switch (output)
        {
            case "JSON":
                return "[\n" + string.Join(",\n", present) + "\n]\n";
            case "XML":
                text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<MediaInfoReports>\n");
                foreach (string report in present)
                    text.Append(report.StartsWith("<?xml", StringComparison.Ordinal) ? report[(report.IndexOf("?>", StringComparison.Ordinal) + 2)..].Trim() : report).Append('\n');
                return text.Append("</MediaInfoReports>\n").ToString();
            case "Text":
                for (int i = 0; i < files.Count; i++)
                {
                    text.Append(files[i]).Append('\n').Append(new string('=', Math.Min(files[i].Length, 100))).Append('\n');
                    text.Append(reports[i] ?? "(MediaInfo can't read this file)\n").Append('\n');
                }
                return text.ToString();
            default:
                return string.Join("\n", present) + "\n";
        }
    }
}

/// <summary>F3 on a video or audio file: MediaInfo's text report in the viewer (search, copy and print come with it).</summary>
internal sealed class MediaInfoViewer : IPluginViewer
{
    private readonly MediaInfoPlugin _plugin;

    public MediaInfoViewer(MediaInfoPlugin plugin) => _plugin = plugin;

    public string Id => "media";
    public string DisplayName => "Video and audio files";
    public string Masks => MediaReport.ViewerMasks;

    public bool CanView(string path) => _plugin.Settings.Viewer && MediaReport.LibraryVersion() != null;

    public async Task<PluginView> ViewAsync(IPluginHost host, string path)
    {
        string? report = await Task.Run(() => MediaReport.Inform(path, _plugin.Settings.Complete));
        return report == null ? PluginView.NotHandled : PluginView.Text(report, Path.GetFileName(path) + " - " + _plugin.T("Media Info"));
    }
}

/// <summary>The plugin's settings (settings.json in its data folder).</summary>
internal sealed class MediaInfoSettings
{
    /// <summary>Every field MediaInfo knows, not only the usual ones.</summary>
    public bool Complete { get; set; }

    /// <summary>F3 on video and audio files shows the report.</summary>
    public bool Viewer { get; set; } = true;

    public static MediaInfoSettings Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<MediaInfoSettings>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string file)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not saved: the settings hold until the program ends
        }
    }
}
