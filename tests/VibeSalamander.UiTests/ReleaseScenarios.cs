// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.UiTests;

/// <summary>
/// What came in 0.7.0 and isn't tried elsewhere: the command line's folders, the hot path list, names into the command
/// line, First Steps, ISO dates, the picture viewer's new keys, the editor's Sort keys.
/// </summary>
public sealed class ReleaseScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    private AppSession Start(Action<AppSettings>? configure = null, string waitFor = "report.txt", IEnumerable<string>? arguments = null)
    {
        var app = new AppSession(s =>
        {
            s.Theme = AppTheme.Dark;
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
            configure?.Invoke(s);
        }, arguments: arguments);
        AppSession.WaitUntil(() => app.Items().Contains(waitFor), "the folder");
        return app;
    }

    /// <summary>The texts of the main window and its popups (menus, dialogs).</summary>
    private static List<string> Texts(AppSession app)
    {
        var texts = new List<string>();
        foreach (AutomationElement root in new[] { (AutomationElement)app.Window, app.Automation.GetDesktop() })
        {
            try
            {
                foreach (AutomationElement e in root.FindAllDescendants(cf => cf.ByControlType(ControlType.Text).Or(cf.ByControlType(ControlType.MenuItem))))
                {
                    try
                    {
                        texts.Add(e.Properties.Name.ValueOrDefault ?? string.Empty);
                    }
                    catch (Exception)
                    {
                        // gone meanwhile
                    }
                }
            }
            catch (Exception)
            {
                // the window is changing
            }
        }
        return texts;
    }

    private static void WaitForText(AppSession app, Func<string, bool> match, string what, double seconds = 10)
    {
        try
        {
            AppSession.WaitUntil(() => Texts(app).Any(match), what, seconds);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " (there: " + string.Join(", ", Texts(app).Where(t => t.Length > 0).Distinct().Take(80)) + ")", ex);
        }
    }

    [UiFact]
    public void Folders_from_the_command_line()
    {
        using AppSession app = Start(waitFor: "holiday 01.png", arguments: new[] { "-L", _files.Photos, "-R", _files.Samples, "-P", "2" });
        AppSession.WaitUntil(() => app.Items(left: false).Contains("config.json"), "the right panel on Samples", 10);
        AppSession.Screenshot("r01 folders from the command line");
        NoErrors(app);
    }

    [UiFact]
    public void Hot_path_list_and_names_into_the_command_line()
    {
        using AppSession app = Start(s =>
        {
            s.HotPaths[3] = _files.Photos;
            s.ShowCommandLine = true;
        });
        app.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.F9, settleMs: 1200);
        WaitForText(app, t => t.Contains(_files.Photos, StringComparison.OrdinalIgnoreCase), "the hot path in the list");
        AppSession.Screenshot("r02 hot path list");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 600);
        app.GoTo("notes.md");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN, settleMs: 800);
        AutomationElement line = app.Window.FindFirstDescendant(cf => cf.ByAutomationId("CommandLine"));
        AppSession.WaitUntil(() => (line.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? string.Empty).Contains("notes.md", StringComparison.Ordinal),
            "the name in the command line", 5);
        NoErrors(app);
    }

    [UiFact]
    public void First_steps_switch_to_total_commander_keys()
    {
        using AppSession app = Start();
        app.Menu("Help", "First Steps...");
        Thread.Sleep(1200);
        AppSession.Screenshot("r03 first steps");
        ComboBox keys = AppSession.Retry(() => app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))?.AsComboBox(), "the shortcuts choice");
        keys.Select(1);
        Thread.Sleep(600);
        app.Click("OK", settleMs: 1500);
        // the F-key bar follows: F2 refreshes in Total Commander
        WaitForText(app, t => t == "Refresh", "Total Commander's F2 in the F-key bar");
        NoErrors(app);
    }

    [UiFact]
    public void Dates_as_iso()
    {
        using AppSession app = Start(s => s.Panels.DateStyle = DateStyle.Iso);
        WaitForText(app, t => t.Contains("2026-03-14", StringComparison.Ordinal), "an ISO date in the panel");
        NoErrors(app);
    }

    [UiFact]
    public void Picture_viewer_fit_width_mirror_and_full_screen()
    {
        using AppSession app = Start(s => s.Left.Path = _files.Photos, waitFor: "holiday 01.png");
        app.GoTo("holiday 01.png");
        app.Press(VirtualKeyShort.F3, settleMs: 300);
        Window viewer = app.OtherWindow("holiday 01.png - ");
        viewer.Focus();
        Thread.Sleep(2500);
        app.Press(VirtualKeyShort.KEY_W, settleMs: 600);
        app.Press(VirtualKeyShort.KEY_M, settleMs: 600);
        app.Press(VirtualKeyShort.F11, settleMs: 1500);
        AppSession.Screenshot("r04 picture full screen mirrored");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 1000); // full screen ends, the viewer stays
        Assert.False(viewer.Properties.IsOffscreen.ValueOrDefault);
        app.Press(VirtualKeyShort.RIGHT, settleMs: 1200); // decoded ahead
        Assert.StartsWith("holiday 02", viewer.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        viewer.Close();
        Thread.Sleep(500);
        NoErrors(app);
    }

    [UiFact]
    public void Editor_sorts_the_keys_of_a_json()
    {
        string path = Path.Combine(_files.Target, "unsorted.json");
        File.WriteAllText(path, "{\n  \"zebra\": 1,\n  \"apple\": 2\n}\n");
        using AppSession app = Start(s => s.Left.Path = _files.Target, waitFor: "unsorted.json");
        app.GoTo("unsorted.json");
        app.Press(VirtualKeyShort.F4, settleMs: 300);
        Window editor = app.OtherWindow("unsorted.json - ");
        Thread.Sleep(6000); // WebView2 and Monaco start
        editor.Focus();
        // the overflow of the toolbar, then Sort keys
        AutomationElement more = AppSession.Retry(() => editor.FindFirstDescendant(cf => cf.ByAutomationId("MoreButton")), "the toolbar's More button");
        more.Click();
        Thread.Sleep(800);
        AutomationElement sort = AppSession.Retry(() => app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Sort keys")), "Sort keys");
        sort.Click();
        Thread.Sleep(1200);
        AppSession.Screenshot("r05 editor keys sorted");
        editor.Focus();
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S, settleMs: 1500);
        AppSession.WaitUntil(() =>
        {
            string text = File.ReadAllText(path);
            return text.IndexOf("apple", StringComparison.Ordinal) is >= 0 and var a && a < text.IndexOf("zebra", StringComparison.Ordinal);
        }, "the keys sorted and saved", 8);
        editor.Close();
        Thread.Sleep(500);
        NoErrors(app);
    }
}
