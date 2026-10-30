// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.WindowsAPI;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.UiTests;

/// <summary>
/// Pictures for the README and the wiki ("doc " screenshots, without the taskbar): menus, plugins, Configuration,
/// dialogs. They are tests too: each one fails when its window or dialog doesn't come up.
/// </summary>
public sealed class DocumentationScreenshots : IDisposable
{
    private readonly DummyFiles _files = new(@"C:\Demo"); // a short, tidy path in the address bar

    public void Dispose() => _files.Dispose();

    private AppSession Start(Action<AppSettings>? configure = null, Action<string>? prepareHome = null)
    {
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Photos;
            s.Theme = AppTheme.Dark; // the pictures are all in the dark theme
            configure?.Invoke(s);
        }, prepareHome);
        AppSession.WaitUntil(() => app.Items().Count > 3, "the panels");
        Thread.Sleep(1500); // icons and thumbnails
        return app;
    }

    private static void Doc(string name) => AppSession.Screenshot("doc " + name, workArea: true);

    [UiFact]
    public void Hero_pictures()
    {
        using (AppSession app = Start(s => s.Right.ViewMode = ViewMode.Thumbnails))
        {
            app.GoTo("report.txt");
            Doc("hero dark");
        }
        using (AppSession app = Start(s => s.Colors.Scheme = "TokyoNight"))
            Doc("scheme tokyo night");
    }

    /// <summary>The five views of the photo panel, for the README's animation.</summary>
    [UiFact]
    public void Views()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.TAB, settleMs: 500); // the photos panel
        var keys = new[] { VirtualKeyShort.KEY_1, VirtualKeyShort.KEY_2, VirtualKeyShort.KEY_3, VirtualKeyShort.KEY_4, VirtualKeyShort.KEY_5 };
        string[] names = { "brief", "detailed", "icons", "thumbnails", "tiles" };
        for (int i = 0; i < keys.Length; i++)
        {
            app.Press(VirtualKeyShort.ALT, keys[i], settleMs: 2000);
            Doc("view " + names[i]);
        }
    }

    [UiFact]
    public void Menus()
    {
        using AppSession app = Start();
        foreach (string menu in new[] { "Files", "Commands", "Options", "Plugins" })
        {
            app.Menu(menu);
            Doc("menu " + menu.ToLowerInvariant());
            app.Press(VirtualKeyShort.ESCAPE, settleMs: 600);
        }
    }

    [UiFact]
    public void Quick_search_and_the_transfer_panel()
    {
        using AppSession app = Start(s =>
        {
            s.AlwaysShowTransfers = true;
            s.Right.Path = _files.Target;
        });
        app.Press(VirtualKeyShort.HOME);
        app.Type("bud");
        Doc("quick search");
        app.Press(VirtualKeyShort.ESCAPE);
        app.GoTo("data.bin");
        app.Press(VirtualKeyShort.F5, settleMs: 800);
        app.Press(VirtualKeyShort.ENTER, settleMs: 2500);
        Doc("transfer panel");
    }

    [UiFact]
    public void Find_with_results()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F7, settleMs: 1500);
        app.OtherWindow("Find Files");
        app.Type("*.txt");
        app.Press(VirtualKeyShort.ENTER, settleMs: 2500);
        Doc("find results");
        app.CloseOtherWindows();
    }

    [UiFact]
    public void Configuration_pages()
    {
        using AppSession app = Start();
        app.Menu("Options", "Configuration...");
        app.OtherWindow("Configuration");
        Thread.Sleep(1000);
        Doc("configuration general");
        foreach (string page in new[] { "Panels", "Views", "Appearance", "Colors", "Keyboard", "Toolbar", "About" })
        {
            app.ClickNamed(page);
            Doc("configuration " + page.ToLowerInvariant());
        }
        app.CloseOtherWindows();
    }

    [UiFact]
    public void User_menu_and_about()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.F9, settleMs: 1200);
        Doc("user menu");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 600);
        app.Menu("Help", "About Vibe Salamander");
        Thread.Sleep(1000);
        Doc("about");
        app.Press(VirtualKeyShort.ESCAPE);
    }

    [UiFact]
    public void Plugin_windows()
    {
        using AppSession app = Start(s => s.Left.Path = _files.Root);
        app.Menu("Plugins", "Plugins Manager...");
        Thread.Sleep(1500);
        Doc("plugins manager");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 800);
        app.CloseOtherWindows();

        app.Menu("Plugins", "FTP / SFTP", "Connect to FTP / SFTP Server...");
        Thread.Sleep(1500);
        Doc("ftp connect");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 800);
        app.CloseOtherWindows();

        app.Menu("Plugins", "Disk Map", "Show disk map");
        Thread.Sleep(4000);
        Doc("disk map blocks");
        foreach (string view in new[] { "Sunburst", "Columns", "Classic" })
        {
            app.ClickNamed(view, settleMs: 2000);
            Doc("disk map " + view.ToLowerInvariant());
        }
        app.CloseOtherWindows();
    }

    [UiFact]
    public void Archive_dialogs()
    {
        using AppSession app = Start(s => s.Right.Path = _files.Target);
        // the Pack dialog first: after a plugin window has closed, the keys don't always reach the panel again
        app.GoTo("report.txt");
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F5, settleMs: 1500);
        Doc("pack");
        try
        {
            app.ClickNamed("Advanced", settleMs: 1200);
            Doc("pack advanced");
        }
        catch (TimeoutException)
        {
            // no Advanced part for this format
        }
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 800);

        app.GoTo("archive.zip");
        app.Menu("Plugins", "Archives", "Test archive");
        Thread.Sleep(2500);
        Doc("test archive");
        app.CloseOtherWindows();
    }

    /// <summary>The example plugin (samples/HelloPlugin) for the wiki's Writing plugins page; VIBE_HELLO_PLUGIN is its DLL.</summary>
    [UiFact]
    public void Example_plugin()
    {
        string? dll = Environment.GetEnvironmentVariable("VIBE_HELLO_PLUGIN");
        if (dll is null || !File.Exists(dll))
            return; // not built on this machine
        using AppSession app = Start(prepareHome: home =>
        {
            string folder = Directory.CreateDirectory(Path.Combine(home, "Plugins", "HelloPlugin")).FullName;
            File.Copy(dll, Path.Combine(folder, "HelloPlugin.dll"));
        });
        app.Menu("Plugins", "Hello");
        Thread.Sleep(800);
        Doc("hello menu");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 400);
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 600);

        app.Menu("Plugins", "Hello", "Largest files here...");
        app.OtherWindow("Largest files");
        Thread.Sleep(2000);
        Doc("hello largest files");
        app.CloseOtherWindows();
    }

    // ---- the viewer and the editor --------------------------------------------------------------------------

    /// <summary>F3 / F4 on a file of the left panel: its window, maximized for the picture.</summary>
    private static FlaUI.Core.AutomationElements.Window OpenWindow(AppSession app, string name, VirtualKeyShort key, int settleMs)
    {
        app.Window.Focus();
        app.GoTo(name);
        app.Press(key, settleMs: 300);
        var window = app.OtherWindow(name + " - ");
        window.Focus();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Thread.Sleep(settleMs);
        return window;
    }

    private static void Close(FlaUI.Core.AutomationElements.Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // closed already
        }
        Thread.Sleep(600);
    }

    [UiFact]
    public void Viewer_and_editor()
    {
        using AppSession app = Start(s => s.Left.Path = _files.Samples);

        var markdown = OpenWindow(app, "README.md", VirtualKeyShort.F3, 4000);
        Doc("viewer markdown");
        Close(markdown);

        var json = OpenWindow(app, "config.json", VirtualKeyShort.F3, 1500);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1, settleMs: 1200);
        Doc("viewer tree");
        Close(json);

        var log = OpenWindow(app, "server.log", VirtualKeyShort.F3, 2000);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F, settleMs: 600);
        app.Type("request 150000 ", settleMs: 300);
        app.Press(VirtualKeyShort.ENTER, settleMs: 2500);
        Doc("viewer big file");
        Close(log);

        var editor = OpenWindow(app, "README.md", VirtualKeyShort.F4, 7000);
        Doc("editor markdown");
        Close(editor);

        var code = OpenWindow(app, "config.json", VirtualKeyShort.F4, 7000);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.END, settleMs: 200);
        app.Type(" oops", settleMs: 2500);
        Doc("editor json problem");
        Close(code);
        try
        {
            app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Don't save").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)))?.Click();
        }
        catch (Exception)
        {
            // the question didn't come
        }
        Thread.Sleep(600);
    }

    [UiFact]
    public void Picture_viewer()
    {
        using AppSession app = Start(s =>
        {
            s.Left.Path = _files.Photos;
            s.Viewer.ShowImageDetails = true;
        });
        var picture = OpenWindow(app, "holiday 01.png", VirtualKeyShort.F3, 3000);
        Doc("viewer picture");
        Close(picture);
    }
}
