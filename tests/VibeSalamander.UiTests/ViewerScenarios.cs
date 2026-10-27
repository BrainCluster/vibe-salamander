// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.UiTests;

/// <summary>The built-in viewer (F3) and editor (F4) on a file of each kind.</summary>
public sealed class ViewerScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    /// <summary>The Samples folder on the left, in the dark theme.</summary>
    private AppSession Start(Action<AppSettings>? configure = null, string firstItem = "config.json")
    {
        var app = new AppSession(s =>
        {
            s.Theme = AppTheme.Dark;
            s.Left.Path = _files.Samples;
            s.Right.Path = _files.Photos;
            configure?.Invoke(s);
        });
        AppSession.WaitUntil(() => app.Items().Contains(firstItem), "the folder");
        return app;
    }

    private string Sample(string name) => Path.Combine(_files.Samples, name);

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    /// <summary>F3 on a file: its viewer window.</summary>
    private static Window View(AppSession app, string name, int settleMs = 2500)
    {
        app.Window.Focus();
        app.GoTo(name);
        app.Press(VirtualKeyShort.F3, settleMs: 300);
        Window viewer = app.OtherWindow(name + " - ");
        viewer.Focus();
        Thread.Sleep(settleMs);
        return viewer;
    }

    private static void Close(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // closed already
        }
        Thread.Sleep(500);
    }

    // ---- the viewer ---------------------------------------------------------------------------------------------

    [UiFact]
    public void A_big_log_in_text_and_hex_with_find()
    {
        using AppSession app = Start();
        Window viewer = View(app, "server.log");
        AppSession.Screenshot("v01 viewer text");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F, settleMs: 600);
        app.Type("request 150000 ", settleMs: 300);
        app.Press(VirtualKeyShort.ENTER, settleMs: 2500);
        AppSession.Screenshot("v02 viewer find");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 300);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2, settleMs: 1500);
        AppSession.Screenshot("v03 viewer hex");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 800);
        AppSession.WaitUntil(() => !Exists(viewer) || viewer.IsOffscreen, "the viewer closed by Esc", 5);
        NoErrors(app);
    }

    [UiFact]
    public void Json_xml_and_yaml_as_trees()
    {
        using AppSession app = Start();
        Window json = View(app, "config.json");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1, settleMs: 1500);
        AppSession.Screenshot("v04 viewer json tree");
        Close(json);
        Window xml = View(app, "layout.xml");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1, settleMs: 1500);
        AppSession.Screenshot("v05 viewer xml tree");
        Close(xml);
        Window yaml = View(app, "pipeline.yaml");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1, settleMs: 1500);
        AppSession.Screenshot("v06 viewer yaml tree");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2, settleMs: 1200);
        AppSession.Screenshot("v07 viewer yaml text");
        Close(yaml);
        NoErrors(app);
    }

    [UiFact]
    public void Markdown_notebook_and_web_page_rendered()
    {
        using AppSession app = Start();
        Window markdown = View(app, "README.md", settleMs: 4000);
        AppSession.Screenshot("v08 viewer markdown");
        Close(markdown);
        Window notebook = View(app, "analysis.ipynb", settleMs: 4000);
        AppSession.Screenshot("v09 viewer notebook");
        Close(notebook);
        Window page = View(app, "page.html", settleMs: 4000);
        AppSession.Screenshot("v10 viewer web page");
        Close(page);
        NoErrors(app);
    }

    [UiFact]
    public void Pictures_go_through_the_folder()
    {
        using AppSession app = Start(s => s.Left.Path = _files.Photos, "holiday 01.png");
        Window picture = View(app, "holiday 01.png");
        AppSession.Screenshot("v11 viewer picture");
        app.Press(VirtualKeyShort.RIGHT, settleMs: 1500);
        app.Press(VirtualKeyShort.KEY_R, settleMs: 1200);
        AppSession.Screenshot("v12 viewer next picture rotated");
        Assert.StartsWith("holiday 02", picture.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Close(picture);
        NoErrors(app);
    }

    [UiFact]
    public void Pdf_and_sound()
    {
        using AppSession app = Start();
        Window pdf = View(app, "document.pdf", settleMs: 3000);
        AppSession.Screenshot("v13 viewer pdf");
        Close(pdf);
        Window sound = View(app, "tone.wav", settleMs: 3000);
        AppSession.Screenshot("v14 viewer sound");
        Close(sound);
        NoErrors(app);
    }

    // ---- the editor ---------------------------------------------------------------------------------------------

    /// <summary>F4 on a file: its editor window, once Monaco (or the plain fallback) has the text.</summary>
    private static Window Edit(AppSession app, string name)
    {
        app.Window.Focus();
        app.GoTo(name);
        app.Press(VirtualKeyShort.F4, settleMs: 300);
        Window editor = app.OtherWindow(name + " - ");
        Thread.Sleep(6000); // WebView2 and Monaco start
        editor.Focus();
        Thread.Sleep(500);
        return editor;
    }

    [UiFact]
    public void Edit_and_save_code()
    {
        using AppSession app = Start();
        Window editor = Edit(app, "Program.cs");
        AppSession.Screenshot("v15 editor code");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME, settleMs: 200);
        app.Type("// edited by the UI test", settleMs: 200);
        app.Press(VirtualKeyShort.ENTER, settleMs: 800);
        AppSession.WaitUntil(() => (editor.Name ?? string.Empty).StartsWith('●'), "the title marking the change", 5);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S, settleMs: 1500);
        AppSession.WaitUntil(() => File.ReadAllText(Sample("Program.cs")).StartsWith("// edited by the UI test", StringComparison.Ordinal), "Program.cs saved");
        AppSession.WaitUntil(() => !(editor.Name ?? string.Empty).StartsWith('●'), "the title without the change mark", 5);
        Close(editor);
        NoErrors(app);
    }

    [UiFact]
    public void Json_problems_show_while_typing()
    {
        using AppSession app = Start();
        Window editor = Edit(app, "config.json");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.END, settleMs: 200);
        app.Type(" unclosed", settleMs: 2000);
        AppSession.Screenshot("v16 editor json problem");
        AppSession.WaitUntil(() => editor.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(t => (t.Name ?? string.Empty).StartsWith("Line ", StringComparison.Ordinal)), "the problem in the status line", 8);
        Close(editor);
        // closing with changes asks
        Button(editor, app, "Don't save").Click();
        Thread.Sleep(800);
        Assert.DoesNotContain("unclosed", File.ReadAllText(Sample("config.json")), StringComparison.Ordinal);
        NoErrors(app);
    }

    [UiFact]
    public void Markdown_with_its_live_preview()
    {
        using AppSession app = Start();
        Window editor = Edit(app, "README.md");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.END, settleMs: 200);
        app.Press(VirtualKeyShort.ENTER, settleMs: 100);
        app.Press(VirtualKeyShort.ENTER, settleMs: 100);
        app.Type("## Typed just now", settleMs: 100);
        app.Press(VirtualKeyShort.ENTER, settleMs: 100);
        app.Type("The preview follows the text.", settleMs: 2500);
        AppSession.Screenshot("v17 editor markdown preview");
        Close(editor);
        Button(editor, app, "Don't save").Click();
        Thread.Sleep(800);
        NoErrors(app);
    }

    private static AutomationElement Button(Window window, AppSession app, string name)
    {
        AutomationElement? found = null;
        AppSession.WaitUntil(() =>
        {
            found = window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))
                    ?? app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)));
            return found != null;
        }, "the button " + name, 8);
        return found!;
    }

    private static bool Exists(AutomationElement element)
    {
        try
        {
            _ = element.Name;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
