// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FlaUI.Core.WindowsAPI;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // one app on the screen at a time

namespace VibeSalamander.UiTests;

/// <summary>
/// What a user does, on dummy files, with a screenshot of each step (the "ui-screenshots" branch shows them).
/// Every run also checks that nothing went into the error log.
/// </summary>
public sealed class Scenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    private AppSession Start(Action<AppSettings>? configure = null) => new(s =>
    {
        s.Left.Path = _files.Documents;
        s.Right.Path = _files.Photos;
        configure?.Invoke(s);
    });

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    [UiFact]
    public void Main_window_on_the_dummy_files()
    {
        using AppSession app = Start();
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents in the left panel");
        AppSession.WaitUntil(() => app.Items(left: false).Any(n => n.StartsWith("holiday", StringComparison.Ordinal)), "the photos in the right panel");
        Assert.Contains("Projects", app.Items());
        Assert.Contains("hidden.txt", app.Items()); // shown: ShowHidden is on
        AppSession.Screenshot("01 main window");
        NoErrors(app);
    }

    [UiFact]
    public void Every_view_on_pictures()
    {
        using AppSession app = Start(s => s.RightPanelActive = true);
        AppSession.WaitUntil(() => app.Items(left: false).Count > 3, "the photos");
        (VirtualKeyShort Key, string Name)[] views =
        {
            (VirtualKeyShort.KEY_1, "02 view brief"), (VirtualKeyShort.KEY_2, "03 view detailed"), (VirtualKeyShort.KEY_3, "04 view icons"),
            (VirtualKeyShort.KEY_4, "05 view thumbnails"), (VirtualKeyShort.KEY_5, "06 view tiles"),
        };
        foreach (var (key, name) in views)
        {
            app.Press(VirtualKeyShort.ALT, key, settleMs: 2500); // thumbnails take a moment
            AppSession.Screenshot(name);
        }
        NoErrors(app);
    }

    [UiFact]
    public void Copy_with_F5()
    {
        using AppSession app = Start(s => s.Right.Path = _files.Target);
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        app.GoTo("report.txt");
        app.Press(VirtualKeyShort.F5, settleMs: 1000);
        AppSession.Screenshot("10 copy dialog");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => File.Exists(Path.Combine(_files.Target, "report.txt")), "report.txt copied to the target");
        AppSession.WaitUntil(() => app.Items(left: false).Contains("report.txt"), "the copy shown in the right panel");
        Assert.Equal(File.ReadAllText(Path.Combine(_files.Documents, "report.txt")), File.ReadAllText(Path.Combine(_files.Target, "report.txt")));
        AppSession.Screenshot("11 after copy");
        NoErrors(app);
    }

    [UiFact]
    public void Create_a_directory_rename_and_delete()
    {
        using AppSession app = Start();
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");

        app.Press(VirtualKeyShort.F7, settleMs: 1000);
        AppSession.Screenshot("12 create directory");
        app.Type("New things");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => Directory.Exists(Path.Combine(_files.Documents, "New things")), "the new directory");

        app.GoTo("notes.md");
        app.Press(VirtualKeyShort.F2, settleMs: 800);
        AppSession.Screenshot("13 rename in place");
        app.Type("renamed"); // the name without its extension is selected
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => File.Exists(Path.Combine(_files.Documents, "renamed.md")), "notes.md renamed to renamed.md");
        Assert.False(File.Exists(Path.Combine(_files.Documents, "notes.md")));

        app.GoTo("renamed.md");
        app.Press(VirtualKeyShort.F8, settleMs: 1000); // to the Recycle Bin, no question (the default)
        AppSession.WaitUntil(() => !File.Exists(Path.Combine(_files.Documents, "renamed.md")), "renamed.md deleted");
        AppSession.Screenshot("14 after delete");
        NoErrors(app);
    }

    [UiFact]
    public void Marking_and_the_context_menu()
    {
        using AppSession app = Start();
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        app.GoTo("budget 2026.csv");
        app.Press(VirtualKeyShort.INSERT); // mark and move down
        app.Press(VirtualKeyShort.INSERT);
        AppSession.Screenshot("20 marked items");
        app.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.F10, settleMs: 1200);
        AppSession.Screenshot("21 context menu");
        app.Press(VirtualKeyShort.ESCAPE);
        NoErrors(app);
    }

    [UiFact]
    public void Into_a_folder_and_back()
    {
        using AppSession app = Start();
        AppSession.WaitUntil(() => app.Items().Contains("Projects"), "the documents");
        app.GoTo("Projects");
        app.Press(VirtualKeyShort.ENTER, settleMs: 1000);
        AppSession.WaitUntil(() => app.Items().Contains("Vibe") && app.Items().Contains("Old"), "the Projects folder");
        AppSession.Screenshot("22 inside a folder");
        app.Press(VirtualKeyShort.HOME);
        app.Press(VirtualKeyShort.ENTER, settleMs: 1000); // ".."
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "back in Documents");
        Assert.Equal("Projects", app.Caret()); // the caret comes back to the folder we left
        NoErrors(app);
    }

    [UiFact]
    public void Find_window()
    {
        using AppSession app = Start();
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F7, settleMs: 1500);
        var find = app.OtherWindow("Find");
        AppSession.Screenshot("30 find window");
        find.Close();
        NoErrors(app);
    }

    [UiFact]
    public void Configuration_window()
    {
        using AppSession app = Start();
        app.Menu("Options", "Configuration...");
        var configuration = app.OtherWindow("Configuration");
        Thread.Sleep(1000);
        AppSession.Screenshot("31 configuration");
        configuration.Close();
        NoErrors(app);
    }

    [UiFact]
    public void This_PC_and_the_dark_theme()
    {
        using AppSession app = Start(s =>
        {
            s.Theme = AppTheme.Dark;
            s.Right.Path = "thispc:";
        });
        AppSession.WaitUntil(() => app.Items(left: false).Count > 0, "the drives");
        Thread.Sleep(1000);
        AppSession.Screenshot("40 this pc dark");
        NoErrors(app);
    }

    [UiFact]
    public void Modern_views_open_picture_folders_as_thumbnails()
    {
        using AppSession app = Start(s =>
        {
            s.ViewStyle = ViewStyle.Modern;
            s.Left.Path = "thispc:";
        });
        Thread.Sleep(2500);
        AppSession.Screenshot("41 modern views");
        NoErrors(app);
    }

    [UiFact]
    public void Croatian()
    {
        using AppSession app = Start(s => s.General.Language = "hr");
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        app.Press(VirtualKeyShort.F5, settleMs: 1000);
        AppSession.Screenshot("42 croatian copy dialog");
        app.Press(VirtualKeyShort.ESCAPE);
        NoErrors(app);
    }

    [UiFact]
    public void A_folder_of_5000_files()
    {
        var clock = Stopwatch.StartNew();
        using AppSession app = Start(s => s.Left.Path = _files.Many);
        AppSession.WaitUntil(() => app.Items().Contains("file 0000.txt"), "the 5000 files", seconds: 30);
        double seconds = clock.Elapsed.TotalSeconds;
        app.Press(VirtualKeyShort.END, settleMs: 800);
        AppSession.WaitUntil(() => app.Caret() == "file 4999.txt", "the caret on the last file");
        AppSession.Screenshot("50 5000 files");
        Assert.True(seconds < 20, $"starting on 5000 files took {seconds:0.0} s");
        NoErrors(app);
    }
}
