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
        app.Calm(300); // icons and thumbnails
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
        app.Press(VirtualKeyShort.TAB); // the photos panel
        var keys = new[] { VirtualKeyShort.KEY_1, VirtualKeyShort.KEY_2, VirtualKeyShort.KEY_3, VirtualKeyShort.KEY_4, VirtualKeyShort.KEY_5 };
        string[] names = { "brief", "detailed", "icons", "thumbnails", "tiles" };
        for (int i = 0; i < keys.Length; i++)
        {
            app.Press(VirtualKeyShort.ALT, keys[i]);
            app.Calm(300); // thumbnails
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
            app.WaitMenu();
            Doc("menu " + menu.ToLowerInvariant());
            app.Press(VirtualKeyShort.ESCAPE);
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
        app.Press(VirtualKeyShort.F5);
        app.WaitDialog();
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => File.Exists(Path.Combine(_files.Target, "data.bin")), "data.bin copied");
        app.WaitTransfers();
        app.Calm(300);
        Doc("transfer panel");
    }

    [UiFact]
    public void Find_with_results()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F7);
        app.OpenedWindow("Find Files");
        app.Type("*.txt");
        app.Press(VirtualKeyShort.ENTER);
        app.Calm(300); // found
        Doc("find results");
        app.CloseOtherWindows();
    }

    [UiFact]
    public void Configuration_pages()
    {
        using AppSession app = Start();
        app.Menu("Options", "Configuration...");
        app.OpenedWindow("Configuration");
        Doc("configuration general");
        foreach (string page in new[] { "Panels", "Views", "Appearance", "Colors", "Keyboard", "Toolbar", "About" })
        {
            app.ClickNamed(page);
            Doc("configuration " + page.ToLowerInvariant());
        }
        app.CloseOtherWindows();
    }

    [UiFact]
    public void Icon_packs()
    {
        using AppSession app = Start(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Root;
            s.Appearance.IconPack = VibeSalamander.Core.Settings.IconPack.Fluent;
        });
        app.Calm(300);
        Doc("icons vibe salamander in color");
    }

    [UiFact]
    public void User_menu_and_about()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.F9, settleMs: 500); // the user menu opens
        Doc("user menu");
        app.Press(VirtualKeyShort.ESCAPE);
        app.Menu("Help", "About Vibe Salamander");
        app.WaitDialog();
        Doc("about");
        app.Press(VirtualKeyShort.ESCAPE);
    }

    [UiFact]
    public void Plugin_windows()
    {
        using AppSession app = Start(s => s.Left.Path = _files.Root);
        app.Menu("Plugins", "Plugins Manager...");
        app.OpenedWindow("Configuration");
        Doc("plugins manager");
        app.Press(VirtualKeyShort.ESCAPE);
        app.CloseOtherWindows();

        app.Menu("Plugins", "FTP / SFTP", "Connect to FTP / SFTP Server...");
        app.WaitDialogOrWindow();
        Doc("ftp connect");
        app.Press(VirtualKeyShort.ESCAPE);
        app.CloseOtherWindows();

        app.Menu("Plugins", "Disk Map", "Show disk map");
        app.Calm(400); // read and drawn
        Doc("disk map blocks");
        foreach (string view in new[] { "Sunburst", "Columns", "Classic" })
        {
            app.ClickNamed(view);
            app.Calm(300); // drawn
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
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F5);
        app.WaitDialog();
        Doc("pack");
        try
        {
            app.ClickNamed("Advanced", settleMs: 500); // it unfolds
            Doc("pack advanced");
        }
        catch (TimeoutException)
        {
            // no Advanced part for this format
        }
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitDialog(open: false);

        app.GoTo("archive.zip");
        app.Menu("Plugins", "Archives", "Test archive");
        app.OpenedWindow("Test Archive");
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
        app.WaitMenu();
        Doc("hello menu");
        app.Press(VirtualKeyShort.ESCAPE);
        app.Press(VirtualKeyShort.ESCAPE);

        app.Menu("Plugins", "Hello", "Largest files here...");
        app.OpenedWindow("Largest files");
        Doc("hello largest files");
        app.CloseOtherWindows();
    }

    // ---- the viewer and the editor --------------------------------------------------------------------------

    /// <summary>F3 / F4 on a file of the left panel: its window, maximized for the picture.</summary>
    private static FlaUI.Core.AutomationElements.Window OpenWindow(AppSession app, string name, VirtualKeyShort key)
    {
        app.Window.Focus();
        app.GoTo(name);
        app.Press(key, settleMs: 300);
        var window = app.OtherWindow(name + " - ");
        window.Focus();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        app.Calm(400); // shown (the viewer and the editor say when), at its new size
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
        Timing.Pause(250);
    }

    [UiFact]
    public void Viewer_and_editor()
    {
        using AppSession app = Start(s => s.Left.Path = _files.Samples);

        var markdown = OpenWindow(app, "README.md", VirtualKeyShort.F3);
        Doc("viewer markdown");
        Close(markdown);

        var json = OpenWindow(app, "config.json", VirtualKeyShort.F3);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        app.Calm(300);
        Doc("viewer tree");
        Close(json);

        var log = OpenWindow(app, "server.log", VirtualKeyShort.F3);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
        app.Type("request 150000 ", settleMs: 300);
        app.Press(VirtualKeyShort.ENTER);
        app.Calm(300); // found
        Doc("viewer big file");
        Close(log);

        var editor = OpenWindow(app, "README.md", VirtualKeyShort.F4);
        Doc("editor markdown");
        Close(editor);

        var code = OpenWindow(app, "config.json", VirtualKeyShort.F4);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.END, settleMs: 200);
        app.Type(" oops");
        AppSession.Soon(() => code.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Any(t => (t.Name ?? string.Empty).StartsWith("Line ", StringComparison.Ordinal)), 20); // Monaco validates after a pause in typing
        Timing.Pause(200);
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
        Timing.Pause(250);
    }

    [UiFact]
    public void Picture_viewer()
    {
        using AppSession app = Start(s =>
        {
            s.Left.Path = _files.Photos;
            s.Viewer.ShowImageDetails = true;
        });
        var picture = OpenWindow(app, "holiday 01.png", VirtualKeyShort.F3);
        Doc("viewer picture");
        Close(picture);
    }
}
