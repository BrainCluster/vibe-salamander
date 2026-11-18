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
