// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.UiTests;

/// <summary>Find with text rows and Show in Panel, Find Duplicates, Compare Directories with Synchronize, transfer limits and graphs.</summary>
public sealed class SearchScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    private AppSession Start(Action<AppSettings>? configure = null, string waitFor = "report.txt")
    {
        var app = new AppSession(s =>
        {
            s.Theme = AppTheme.Dark;
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
            configure?.Invoke(s);
        });
        AppSession.WaitUntil(() => app.Items().Contains(waitFor), "the folder");
        return app;
    }

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    private static AutomationElement Named(AutomationElement root, string name, ControlType type, double seconds = 10)
    {
        AutomationElement? found = null;
        AppSession.WaitUntil(() =>
        {
            found = root.FindAllDescendants(cf => cf.ByControlType(type))
                .FirstOrDefault(e => (e.Name ?? string.Empty).StartsWith(name, StringComparison.OrdinalIgnoreCase));
            return found != null;
        }, name, seconds);
        return found!;
    }

    [UiFact]
    public void Find_text_rows_and_show_in_panel()
    {
        using AppSession app = Start(s => s.LastSearch = new SavedSearch
        {
            Named = "*.*",
            Texts = { new SavedText { Text = "report" }, new SavedText { Text = "zzz-not-there", Negate = true } },
            SkipFolders = "node_modules",
        });
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F7, settleMs: 1500);
        Window find = app.OtherWindow("Find Files");
        app.Press(VirtualKeyShort.ENTER, settleMs: 3000);
        AppSession.Screenshot("s01 find with text rows");
        AutomationElement show = Named(find, "Show in Panel", ControlType.Button);
        AppSession.WaitUntil(() => show.IsEnabled, "results to show", 15);
        show.Click();
        Thread.Sleep(1500);
        AppSession.Screenshot("s02 results in the panel");
        AppSession.WaitUntil(() => app.Items().Any(n => n.Contains("report", StringComparison.OrdinalIgnoreCase)), "the found file in the panel");
        find.Close();
        NoErrors(app);
    }

    [UiFact]
    public void Find_duplicates_and_select_the_copies()
    {
        string content = string.Concat(Enumerable.Repeat("the same bytes ", 5000));
        File.WriteAllText(Path.Combine(_files.Target, "photo.txt"), content);
        File.WriteAllText(Path.Combine(_files.Target, "photo (copy).txt"), content);
        File.WriteAllText(Path.Combine(_files.Target, "other.txt"), content + "!");
        using AppSession app = Start(s => s.Left.Path = _files.Target, waitFor: "photo.txt");
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F7);
        Thread.Sleep(1500);
        Window duplicates = app.OtherWindow("Find Duplicate");
        Named(duplicates, "Find", ControlType.Button).Click();
        AppSession.WaitUntil(() => duplicates.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(t => (t.Name ?? string.Empty).Contains("photo (copy).txt", StringComparison.Ordinal)), "the duplicates", 20);
        Named(duplicates, "Select duplicates", ControlType.Button).Click();
        Thread.Sleep(800);
        Named(app.Automation.GetDesktop(), "All but the newest", ControlType.MenuItem).Click();
        Thread.Sleep(800);
        AppSession.Screenshot("s03 duplicates");
        Assert.Contains(duplicates.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), t => (t.Name ?? string.Empty).StartsWith("Delete 1 selected", StringComparison.Ordinal));
        duplicates.Close();
        NoErrors(app);
    }

    [UiFact]
    public void Compare_and_synchronize()
    {
        File.WriteAllText(Path.Combine(_files.Target, "only on the right.txt"), "x");
        using AppSession app = Start(s => s.Compare = new Core.Tools.CompareOptions { ByDate = true, BySize = true, Subdirectories = true, ShowList = true });
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F10, settleMs: 1200);
        app.Press(VirtualKeyShort.ENTER, settleMs: 2500); // the dialog's Compare
        Window compare = app.OtherWindow("Compare Directories");
        AppSession.WaitUntil(() => compare.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(t => (t.Name ?? string.Empty).Contains("report.txt", StringComparison.Ordinal)), "the differences", 20);
        AppSession.Screenshot("s04 compare list");
        Named(compare, "Synchronize (", ControlType.Button).Click();
        Thread.Sleep(1000);
        app.Press(VirtualKeyShort.ENTER); // the dialog's Synchronize
        AppSession.WaitUntil(() => File.Exists(Path.Combine(_files.Target, "report.txt")), "report.txt copied to the right", 30);
        Thread.Sleep(2500);
        AppSession.Screenshot("s05 after synchronizing");
        // one way without mirroring: what is only on the right stays
        Assert.True(File.Exists(Path.Combine(_files.Target, "only on the right.txt")));
        compare.Close();
        NoErrors(app);
    }

    /// <summary>The copy is complete: full size, and no longer open for writing.</summary>
    private static bool Finished(string path, long size)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == size;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [UiFact]
    public void A_speed_limit_and_the_graph()
    {
        string big = Path.Combine(_files.Documents, "big.bin");
        File.WriteAllBytes(big, new byte[24 * 1024 * 1024]);
        using AppSession app = Start(s =>
        {
            s.TransfersGraphs = true;
            s.TransfersSpeedLimit = 4 * 1024 * 1024; // 4 MB/s: the copy takes about 6 s
            s.AskCopyMoveTarget = false;
        }, waitFor: "big.bin");
        app.GoTo("big.bin");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        app.Press(VirtualKeyShort.F5, settleMs: 3500);
        AppSession.Screenshot("s06 transfer graph and limit");
        string copy = Path.Combine(_files.Target, "big.bin");
        AppSession.WaitUntil(() => Finished(copy, 24 * 1024 * 1024), "the copy to finish", 40);
        // 24 MB at 4 MB/s: about 6 s (the first half second is free); without the limit it takes well under a second
        Assert.True(clock.Elapsed.TotalSeconds > 4, $"The copy took {clock.Elapsed.TotalSeconds:0.0} s: the limit didn't hold");
        NoErrors(app);
    }
}
