// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;

namespace VibeSalamander.Plugins.MediaInfo;

/// <summary>One section of a report: "General", "Video", "Audio #2", with its fields in MediaInfo's order.</summary>
internal sealed record MediaSection(string Title, IReadOnlyList<(string Field, string Value)> Fields);

/// <summary>
/// What MediaInfo's library says about a file: its text report (as the MediaInfo app's Text view), the same in
/// HTML, XML or JSON, and the report read back as sections of fields.
/// </summary>
internal static class MediaReport
{
    /// <summary>The report formats Save Report offers: name, file extension, MediaInfo's Output option.</summary>
    public static readonly (string Name, string Extension, string Output)[] Formats =
    {
        ("Text", ".txt", "Text"),
        ("HTML", ".html", "HTML"),
        ("XML", ".xml", "XML"),
        ("JSON", ".json", "JSON"),
    };

    private static readonly object Gate = new();
    private static bool _resolverSet;

    /// <summary>
    /// MediaInfo.dll for this processor: the app keeps it in lib\MediaInfo (next to the program or, in tests, next to
    /// the test assembly); otherwise Windows' usual search.
    /// </summary>
    private static void UseOwnLibrary()
    {
        lock (Gate)
        {
            if (_resolverSet)
                return;
            _resolverSet = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(MediaInfoLib.MediaInfo).Assembly, (name, _, _) =>
                {
                    if (!name.Contains("MediaInfo", StringComparison.OrdinalIgnoreCase))
                        return IntPtr.Zero;
                    foreach (string folder in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(MediaReport).Assembly.Location) ?? string.Empty })
                    {
                        string own = Path.Combine(folder, "lib", "MediaInfo", "MediaInfo.dll");
                        if (File.Exists(own) && NativeLibrary.TryLoad(own, out IntPtr handle))
                            return handle;
                    }
                    return IntPtr.Zero;
                });
            }
            catch (InvalidOperationException)
            {
                // a resolver was set already (another copy of this plugin): it decides
            }
        }
    }

    private static string? _version;
    private static bool _versionAsked;

    /// <summary>MediaInfo's version ("26.01"), or null when its library can't be loaded (asked once).</summary>
    public static string? LibraryVersion()
    {
        lock (Gate)
        {
            if (!_versionAsked)
            {
                _version = ReadVersion();
                _versionAsked = true;
            }
            return _version;
        }
    }

    private static string? ReadVersion()
    {
        try
        {
            UseOwnLibrary();
            using var info = new MediaInfoLib.MediaInfo();
            string text = info.Option("Info_Version");
            return text.Replace("MediaInfoLib - v", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The report of a file in one of <see cref="Formats"/> ("Text": MediaInfo's text view); <paramref name="complete"/>
    /// adds every field MediaInfo knows (its "Complete" option). Null: MediaInfo can't open the file.
    /// </summary>
    public static string? Inform(string path, bool complete, string output = "Text")
    {
        UseOwnLibrary();
        using var info = new MediaInfoLib.MediaInfo();
        info.Option("Complete", complete ? "1" : "0");
        info.Option("Output", output);
        if (info.Open(path) == 0)
            return null;
        try
        {
            return info.Inform().Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";
        }
        finally
        {
            info.Close();
        }
    }

    /// <summary>MediaInfo's text report read back: a line without " : " starts a section, the others are its fields.</summary>
    public static IReadOnlyList<MediaSection> Sections(string text)
    {
        var sections = new List<MediaSection>();
        string? title = null;
        var fields = new List<(string, string)>();
        void Finish()
        {
            if (title != null)
                sections.Add(new MediaSection(title, fields.ToList()));
            fields.Clear();
        }
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0)
            {
                Finish();
                title = line.Trim();
                continue;
            }
            title ??= "General";
            fields.Add((line[..colon].Trim(), line[(colon + 3)..].Trim()));
        }
        Finish();
        return sections;
    }

    /// <summary>Video and audio files: what F3 shows as a report (pictures keep the picture viewer).</summary>
    public const string ViewerMasks =
        "*.mkv;*.mk3d;*.webm;*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.asf;*.flv;*.f4v;*.mpg;*.mpeg;*.m2v;*.ts;*.m2ts;*.mts;*.vob;" +
        "*.3gp;*.3g2;*.ogv;*.rm;*.rmvb;*.divx;*.y4m;*.mxf;" +
        "*.mp3;*.mp2;*.flac;*.wav;*.w64;*.m4a;*.m4b;*.aac;*.ogg;*.oga;*.opus;*.wma;*.ac3;*.eac3;*.dts;*.ape;*.wv;*.aiff;*.aif;" +
        "*.mka;*.mpc;*.tta;*.amr;*.au;*.caf;*.dsf;*.dff";
}
