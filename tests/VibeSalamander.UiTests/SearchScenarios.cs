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

    /// <summary>
    /// A control in one of the app's windows. The window is looked up again from its handle (an owned window found
    /// through the main window doesn't always give its content).
    /// </summary>
    private static AutomationElement Named(AppSession app, AutomationElement window, string name, ControlType type, double seconds = 10)
    {
        AutomationElement? found = null;
        WaitUntilOr(() =>
        {
            foreach (AutomationElement root in Roots(app, window))
            {
                try
                {
                    found = root.FindAllDescendants(cf => cf.ByControlType(type))
                        .FirstOrDefault(e => NameOf(e).StartsWith(name, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception)
                {
                    found = null; // the window is changing: next time
                }
                if (found != null)
                    return true;
            }
            return false;
        }, name, seconds, () => string.Join(", ", Roots(app, window).SelectMany<AutomationElement, string>(r =>
        {
            try
            {
                return r.FindAllDescendants(cf => cf.ByControlType(type)).Select(e => "'" + NameOf(e) + "'").ToList();
            }
            catch (Exception ex)
            {
                return new[] { ex.GetType().Name };
            }
        }).Distinct().Take(60)));
        return found!;
    }

    /// <summary>An element's name, or "" (some elements don't have the property, and reading it throws).</summary>
    private static string NameOf(AutomationElement element)
    {
        try
        {
            return element.Properties.Name.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void WaitUntilOr(Func<bool> condition, string what, double seconds, Func<string> seen)
    {
        try
        {
            AppSession.WaitUntil(condition, what, seconds);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " (there: " + seen() + ")", ex);
        }
    }

    private static List<AutomationElement> Roots(AppSession app, AutomationElement window)
    {
        var roots = new List<AutomationElement>();
        try
        {
            IntPtr handle = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle != IntPtr.Zero)
                roots.Add(app.Automation.FromHandle(handle));
        }
        catch (Exception)
        {
            // gone or not ready: the element itself
        }
        roots.Add(window);
        return roots;
    }

    private static string Texts(AppSession app, AutomationElement window) => string.Join(", ", Roots(app, window).SelectMany<AutomationElement, string>(r =>
    {
        try
        {
            return r.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(e => "'" + NameOf(e) + "'").ToList();
        }
        catch (Exception ex)
        {
            return new[] { ex.GetType().Name };
        }
    }).Distinct().Take(60));

    /// <summary>A text in the window that contains <paramref name="part"/>.</summary>
    private static bool HasText(AppSession app, AutomationElement window, string part)
    {
        foreach (AutomationElement root in Roots(app, window))
        {
            try
            {
                if (root.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(t => NameOf(t).Contains(part, StringComparison.Ordinal)))
                    return true;
            }
            catch (Exception)
            {
                // the window is changing: next time
            }
        }
        return false;
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
        AutomationElement show = Named(app, find, "Show in Panel", ControlType.Button);
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
        Named(app, duplicates, "Find", ControlType.Button).Click();
        WaitUntilOr(() => HasText(app, duplicates, "photo (copy).txt"), "the duplicates", 20, () => Texts(app, duplicates));
        Named(app, duplicates, "Select duplicates", ControlType.Button).Click();
        Thread.Sleep(800);
        Named(app, app.Automation.GetDesktop(), "All but the newest", ControlType.MenuItem).Click();
        Thread.Sleep(800);
        AppSession.Screenshot("s03 duplicates");
        AppSession.WaitUntil(() => HasText(app, duplicates, "Delete 1 selected"), "one copy selected", 5);
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
        WaitUntilOr(() => HasText(app, compare, "report.txt"), "the differences", 20, () => Texts(app, compare));
        AppSession.Screenshot("s04 compare list");
        Named(app, compare, "Synchronize (", ControlType.Button).Click();
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

    [UiFact]
    public void Compare_two_files()
    {
        string common = "using System;\n\nnamespace Demo;\n\npublic static class Program\n{\n    public static void Main()\n    {\n";
        File.WriteAllText(Path.Combine(_files.Documents, "Program.cs"), common + "        Console.WriteLine(\"Hello\");\n    }\n}\n");
        File.WriteAllText(Path.Combine(_files.Target, "Program.cs"), common + "        Console.WriteLine(\"Hello, world!\");\n        return;\n    }\n}\n");
        using AppSession app = Start(waitFor: "Program.cs");
        app.GoTo("Program.cs");
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C);
        Thread.Sleep(1500);
        Window compare = app.OtherWindow("Program.cs ↔");
        Thread.Sleep(6000); // WebView2 and Monaco start
        AppSession.Screenshot("s07 compare files");
        WaitUntilOr(() => HasText(app, compare, "Difference") || HasText(app, compare, "differences"), "the differences counted", 15,
            () => Texts(app, compare));
        compare.Close();
        NoErrors(app);
    }

    [UiFact]
    public void Duplicate_a_file_in_place()
    {
        File.WriteAllText(Path.Combine(_files.Target, "plan.txt"), "the plan");
        using AppSession app = Start(s => s.Left.Path = _files.Target, waitFor: "plan.txt");
        app.GoTo("plan.txt");
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_D);
        Thread.Sleep(1500);
        AppSession.Screenshot("g01 duplicate named in place");
        app.Press(VirtualKeyShort.ENTER, settleMs: 1000); // keeps the suggested name
        string copy = Path.Combine(_files.Target, "plan (2).txt");
        AppSession.WaitUntil(() => File.Exists(copy), "the copy", 10);
        Assert.Equal("the plan", File.ReadAllText(copy));
        NoErrors(app);
    }

    [UiFact]
    public void Multi_rename_with_a_counter()
    {
        File.WriteAllText(Path.Combine(_files.Target, "a1.txt"), "1");
        File.WriteAllText(Path.Combine(_files.Target, "a2.txt"), "2");
        using AppSession app = Start(s => s.Left.Path = _files.Target, waitFor: "a2.txt");
        app.GoTo("a1.txt");
        app.Press(VirtualKeyShort.INSERT);
        app.Press(VirtualKeyShort.INSERT);
        app.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.F2, settleMs: 1500);
        Window rename = app.OtherWindow("Multi-Rename");
        app.Type("trip [C]", settleMs: 1200);
        AppSession.Screenshot("g02 multi-rename");
        WaitUntilOr(() => HasText(app, rename, "trip 2.txt"), "the new names", 10, () => Texts(app, rename));
        Named(app, rename, "Rename", ControlType.Button).Click();
        AppSession.WaitUntil(() => File.Exists(Path.Combine(_files.Target, "trip 1.txt")) && File.Exists(Path.Combine(_files.Target, "trip 2.txt")),
            "the files renamed", 10);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_files.Target, "trip 1.txt")));
        NoErrors(app);
    }

    [UiFact]
    public void Flat_view_and_quick_view()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_B, settleMs: 2000);
        AppSession.WaitUntil(() => app.Items().Contains(@"Projects\Old\readme.txt"), "the files of the subfolders", 10);
        AppSession.Screenshot("g03 flat view");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_B, settleMs: 1500); // back
        AppSession.WaitUntil(() => app.Items().Contains("notes.md"), "the folder again", 10);
        app.GoTo("notes.md");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Q, settleMs: 1500);
        WaitUntilOr(() => HasText(app, app.Window, "- one"), "the quick view of notes.md", 10, () => Texts(app, app.Window));
        AppSession.Screenshot("g04 quick view");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Q, settleMs: 800);
        NoErrors(app);
    }
}
