// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace VibeSalamander.UiTests;

/// <summary>Tabs in each panel: opening them (here and in the other panel), switching, marks kept, locking, closing.</summary>
public sealed class TabsScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    /// <summary>Documents on the left, the empty Target folder on the right.</summary>
    private AppSession Start()
    {
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
        });
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        return app;
    }

    /// <summary>The tab names of a panel, left to right (none: the row isn't shown).</summary>
    private static List<string> Tabs(AppSession app, bool left = true)
    {
        double middle = app.Window.BoundingRectangle.Left + app.Window.BoundingRectangle.Width / 2.0;
        return app.Window.FindAllDescendants(cf => cf.ByAutomationId("PaneTab"))
            .Where(t => !t.IsOffscreen && (t.BoundingRectangle.Left < middle) == left)
            .OrderBy(t => t.BoundingRectangle.Left)
            .Select(t => t.Name)
            .ToList();
    }

    private static void WaitTabs(AppSession app, string what, bool left, params string[] names) =>
        AppSession.WaitUntil(() => Tabs(app, left).SequenceEqual(names), what + ": " + string.Join(", ", names));

    private static void CtrlShift(AppSession app, VirtualKeyShort key)
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, key);
        app.Calm();
    }

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    [UiFact]
    public void Tabs_open_switch_keep_their_marks_lock_and_close()
    {
        using AppSession app = Start();
        Assert.Empty(Tabs(app)); // one tab: no row, the panel looks as before

        // Ctrl+Up: the folder under the caret in a new tab
        app.GoTo("Projects");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.UP);
        AppSession.WaitUntil(() => app.Items().Contains("Vibe"), "Projects in the new tab");
        WaitTabs(app, "two tabs", true, "Documents", "Projects");

        // back to the first tab: its folder, with a mark made there kept while the other tab is shown
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "Documents again");
        app.GoTo("report.txt");
        app.Press(VirtualKeyShort.INSERT); // marks it
        AppSession.WaitUntil(() => app.InfoLine().StartsWith("Selected:", StringComparison.Ordinal), "report.txt marked");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        AppSession.WaitUntil(() => app.Items().Contains("Vibe"), "Projects again");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        AppSession.WaitUntil(() => app.Items().Contains("report.txt") && app.InfoLine().StartsWith("Selected:", StringComparison.Ordinal),
            "Documents with report.txt still marked");

        // Ctrl+Shift+Up: a folder in a new tab of the other panel; this panel keeps the keyboard
        app.GoTo("Empty folder");
        CtrlShift(app, VirtualKeyShort.UP);
        WaitTabs(app, "the right panel's tabs", false, "Target", "Empty folder");
        AppSession.Screenshot("65 tabs");

        // a locked tab keeps its folder: opening a folder from it opens a new tab
        app.Menu("Left", "Tabs", "Lock / Unlock Tab");
        app.GoTo("Projects");
        app.Press(VirtualKeyShort.ENTER);
        WaitTabs(app, "a new tab next to the locked one", true, "Documents", "Projects", "Projects");
        AppSession.WaitUntil(() => app.Items().Contains("Vibe"), "Projects shown");

        // Ctrl+Shift+W closes the shown tab; the locked Documents is still there
        CtrlShift(app, VirtualKeyShort.KEY_W);
        WaitTabs(app, "the new tab closed", true, "Documents", "Projects");
        NoErrors(app);
    }

    /// <summary>The arrow at the end of a panel's drive bar.</summary>
    private static AutomationElement TabRowArrow(AppSession app, bool left = true)
    {
        double middle = app.Window.BoundingRectangle.Left + app.Window.BoundingRectangle.Width / 2.0;
        return AppSession.Retry(() => app.Window.FindAllDescendants(cf => cf.ByAutomationId("TabRowButton"))
            .FirstOrDefault(b => (b.BoundingRectangle.Left < middle) == left), "the tab row arrow");
    }

    [UiFact]
    public void The_arrow_at_the_end_of_the_drive_bar_opens_and_closes_the_row_of_tabs()
    {
        using AppSession app = Start();
        Assert.Equal("Show tabs", TabRowArrow(app).Name);

        TabRowArrow(app).Click(); // opens it with the one tab
        WaitTabs(app, "the row opened", true, "Documents");
        AppSession.WaitUntil(() => TabRowArrow(app).Name == "Hide tabs", "the arrow now closes it");
        AppSession.Screenshot("66 tab row opened");

        CtrlShift(app, VirtualKeyShort.KEY_T);
        WaitTabs(app, "two tabs", true, "Documents", "Documents");
        TabRowArrow(app).Click(); // closes it: the tabs stay, Ctrl+Tab still switches
        AppSession.WaitUntil(() => Tabs(app).Count == 0, "the row closed");
        // with the row closed, a badge on the arrow says how many tabs there are
        AppSession.WaitUntil(() => TabRowArrow(app).Name == "Show tabs (2 open)", "the badge with 2 tabs");
        AppSession.Screenshot("68 tab count badge");
        app.GoTo("Projects");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => app.Items().Contains("Vibe"), "Projects in the second tab");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the first tab, with the row closed");
        Assert.Empty(Tabs(app));

        app.GoTo("Projects");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.UP); // a new tab opens the row again
        WaitTabs(app, "the row back with the new tab", true, "Documents", "Projects", "Projects");
        AppSession.WaitUntil(() => TabRowArrow(app).Name == "Hide tabs", "no badge with the row open");
        NoErrors(app);
    }

    private static AutomationElement? Find(AppSession app, string id, bool left = true)
    {
        double middle = app.Window.BoundingRectangle.Left + app.Window.BoundingRectangle.Width / 2.0;
        return app.Window.FindAllDescendants(cf => cf.ByAutomationId(id))
            .FirstOrDefault(b => !b.IsOffscreen && (b.BoundingRectangle.Left < middle) == left);
    }

    [UiFact]
    public void Always_showing_the_row_has_no_arrow_on_the_drive_bar()
    {
        using var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
            s.Panels.AlwaysShowTabs = true;
        });
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");

        WaitTabs(app, "the row with one tab", true, "Documents");
        WaitTabs(app, "the other panel's row too", false, "Target");
        Assert.Null(Find(app, "TabRowButton"));
        Assert.Null(Find(app, "TabRowButton", left: false));
        NoErrors(app);
    }

    [UiFact]
    public void A_full_row_of_tabs_scrolls_with_arrows_at_both_ends()
    {
        using AppSession app = Start();
        Assert.Null(Find(app, "TabsLeftButton"));
        for (int i = 0; i < 14; i++)
            CtrlShift(app, VirtualKeyShort.KEY_T); // fifteen "Documents" tabs: more than the half window holds

        AutomationElement left = AppSession.Retry(() => Find(app, "TabsLeftButton"), "the left arrow");
        AutomationElement right = AppSession.Retry(() => Find(app, "TabsRightButton"), "the right arrow");

        // the newest tab (shown, the last) is scrolled into view, whole, between the arrows
        bool Whole(AutomationElement tab) =>
            tab.BoundingRectangle.Left >= left.BoundingRectangle.Right - 1 && tab.BoundingRectangle.Right <= right.BoundingRectangle.Left + 1;
        List<AutomationElement> TabsHere() => app.Window.FindAllDescendants(cf => cf.ByAutomationId("PaneTab"))
            .Where(t => t.BoundingRectangle.Top < left.BoundingRectangle.Bottom && t.BoundingRectangle.Bottom > left.BoundingRectangle.Top &&
                        t.BoundingRectangle.Left < right.BoundingRectangle.Right)
            .ToList();
        string Describe() => string.Join("; ", TabsHere().Select(t => $"{t.BoundingRectangle.Left}-{t.BoundingRectangle.Right} '{t.Properties.ItemStatus.ValueOrDefault}'")) +
                             $" | arrows {left.BoundingRectangle.Right} / {right.BoundingRectangle.Left}";
        bool shownWhole = false;
        for (int i = 0; i < 50 && !shownWhole; i++)
        {
            shownWhole = TabsHere().FirstOrDefault(t => t.Properties.ItemStatus.ValueOrDefault == "shown") is { } shown && Whole(shown);
            if (!shownWhole)
                Timing.Pause(100);
        }
        Assert.True(shownWhole, "the shown (last) tab isn't whole in view: " + Describe());
        AppSession.WaitUntil(() => left.IsEnabled && !right.IsEnabled, "scrolled to the end: left arrow on, right arrow off");
        AppSession.Screenshot("67 full row of tabs");

        // the left arrow goes back to the first tab
        for (int i = 0; i < 30 && left.IsEnabled; i++)
            left.Click();
        AppSession.WaitUntil(() => !left.IsEnabled && right.IsEnabled, "at the start: left arrow off, right arrow on");
        AutomationElement first = TabsHere().OrderBy(t => t.BoundingRectangle.Left).First();
        Assert.True(Whole(first), $"the first tab isn't whole in view: {first.BoundingRectangle} between {left.BoundingRectangle} and {right.BoundingRectangle}");
        NoErrors(app);
    }

    [UiFact]
    public void Ctrl_U_swaps_the_panels_with_their_tabs()
    {
        using AppSession app = Start();
        CtrlShift(app, VirtualKeyShort.KEY_T); // a second tab of Documents
        WaitTabs(app, "two tabs", true, "Documents", "Documents");

        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_U);

        WaitTabs(app, "the tabs now on the right", false, "Documents", "Documents");
        Assert.Empty(Tabs(app, left: true));
        AppSession.WaitUntil(() => app.Items(left: false).Contains("report.txt"), "Documents on the right");
        NoErrors(app);
    }
}
