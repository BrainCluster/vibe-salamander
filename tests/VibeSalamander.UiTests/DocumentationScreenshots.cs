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

    private AppSession Start(Action<AppSettings>? configure = null)
    {
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Photos;
            configure?.Invoke(s);
        });
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
            Doc("hero light");
        }
        using (AppSession app = Start(s =>
        {
            s.Theme = AppTheme.Dark;
            s.Right.ViewMode = ViewMode.Thumbnails;
        }))
        {
            app.GoTo("report.txt");
            Doc("hero dark");
        }
        using (AppSession app = Start(s => s.Colors.Scheme = "TokyoNight"))
            Doc("scheme tokyo night");
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
        foreach (string page in new[] { "Panels", "Views", "Appearance", "Colors", "Keyboard", "Toolbar" })
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
        app.GoTo("archive.zip");
        app.Menu("Plugins", "Archives", "Test archive");
        Thread.Sleep(2500);
        Doc("test archive");
        app.CloseOtherWindows();

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
        app.Press(VirtualKeyShort.ESCAPE);
    }
}
