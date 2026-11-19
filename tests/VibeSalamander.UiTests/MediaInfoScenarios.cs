// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace VibeSalamander.UiTests;

/// <summary>The Media Info plugin (a separate download, installed into the test's Plugins folder): its window, its item in the right-click menu, F3.</summary>
public sealed class MediaInfoScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    /// <summary>A second of 16-bit stereo PCM at 44.1 kHz.</summary>
    private string Wave()
    {
        string path = Path.Combine(_files.Documents, "tone.wav");
        const int rate = 44100;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + rate * 4);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(rate * 4);
        for (int i = 0; i < rate; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            w.Write(s);
            w.Write(s);
        }
        return path;
    }

    /// <summary>The plugin's folder (build\publish-mediainfo-plugin.ps1), or null when it isn't built here.</summary>
    private static string? PluginFolder() =>
        Environment.GetEnvironmentVariable("VIBE_MEDIAINFO_PLUGIN") is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(from))
            CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    private AppSession? Start()
    {
        if (PluginFolder() is not string plugin)
            return null; // not built on this machine
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
        }, prepareHome: home => CopyFolder(plugin, Path.Combine(home, "Plugins", "Media Info")));
        AppSession.WaitUntil(() => app.Items().Contains("tone.wav"), "the sound file");
        return app;
    }

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    [UiFact]
    public void Media_Info_shows_a_sound_files_details()
    {
        Wave();
        using AppSession? app = Start();
        if (app == null)
            return;
        app.GoTo("tone.wav");
        // the right-click menu of a sound file offers it
        AppSession.RightClick(app.OnName("tone.wav"));
        AppSession.Screenshot("85 media info in the right-click menu");
        app.ClickNamed("Media Info...");
        Window window = app.OtherWindow("Media Info - tone.wav");
        foreach (string shown in new[] { "General", "Audio", "PCM", "44.1 kHz", "2 channels" })
            AppSession.Retry(() => window.FindFirstDescendant(cf => cf.ByName(shown)), $"\"{shown}\" in the Media Info window");
        AppSession.Screenshot("86 media info");
        window.Close();
        NoErrors(app);
    }

    [UiFact]
    public void Media_Info_is_in_the_Plugins_menu_and_not_on_text_files()
    {
        Wave();
        using AppSession? app = Start();
        if (app == null)
            return;
        app.GoTo("tone.wav");
        app.Menu("Plugins", "Media Info", "Media Info...");
        app.OtherWindow("Media Info - tone.wav").Close();
        // a text file's right-click menu doesn't offer it
        AppSession.RightClick(app.OnName("report.txt"));
        app.Calm(300);
        Assert.Null(app.Window.FindFirstDescendant(cf => cf.ByName("Media Info...")));
        app.Press(VirtualKeyShort.ESCAPE);
        NoErrors(app);
    }

    [UiFact]
    public void F3_on_a_sound_file_shows_its_media_information()
    {
        Wave();
        using AppSession? app = Start();
        if (app == null)
            return;
        app.GoTo("tone.wav");
        app.Press(VirtualKeyShort.F3, settleMs: 400);
        Window viewer = app.OtherWindow("tone.wav - Media Info");
        app.Calm(400);
        AppSession.Screenshot("87 media info in the viewer");
        string shown = Directory.GetFiles(Path.Combine(Path.GetTempPath(), "VibeSalamander", "PluginViews"), "tone.wav - Media Info.txt", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).Select(File.ReadAllText).First();
        Assert.Matches(@"Sampling rate\s+: 44\.1 kHz", shown);
        viewer.Close();
        NoErrors(app);
    }
}
