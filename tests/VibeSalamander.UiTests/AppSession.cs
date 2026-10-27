// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core;
using Application = FlaUI.Core.Application;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.UiTests;

/// <summary>
/// One run of Vibe Salamander for a test: its own settings folder (VIBE_SALAMANDER_HOME), the panels on the dummy
/// files, a maximized window. Keys go to the app like a user's; screenshots go to VIBE_SCREENSHOTS.
/// </summary>
public sealed class AppSession : IDisposable
{
    public static string? AppExe
    {
        get
        {
            if (Environment.GetEnvironmentVariable("VIBE_APP_EXE") is { Length: > 0 } exe && File.Exists(exe))
                return exe;
            // a Release x64 build next to the tests (a local run)
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "VibeSalamander.sln")))
                dir = Path.GetDirectoryName(dir);
            string bin = dir == null ? string.Empty : Path.Combine(dir, "src", "VibeSalamander.App", "bin", "x64", "Release");
            return Directory.Exists(bin) ? Directory.EnumerateFiles(bin, "VibeSalamander.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        }
    }

    public static string ScreenshotFolder
    {
        get
        {
            string folder = Environment.GetEnvironmentVariable("VIBE_SCREENSHOTS") is { Length: > 0 } s ? s : Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public string Home { get; }
    public Application App { get; }
    public UIA3Automation Automation { get; } = new();
    public Window Window { get; private set; }

    /// <param name="prepareHome">Called with the settings folder before the app starts (to put a plugin into it).</param>
    public AppSession(Action<AppSettings> configure, Action<string>? prepareHome = null)
    {
        Home = Path.Combine(Path.GetTempPath(), "vibe-ui-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Home);
        prepareHome?.Invoke(Home);
        var settings = new AppSettings { WindowMaximized = true, ShowHidden = true };
        settings.General.Language = "en";
        settings.General.SingleInstance = false;
        configure(settings);
        SettingsStore.Save(settings, Path.Combine(Home, "settings.json"));

        var start = new ProcessStartInfo(AppExe ?? throw new InvalidOperationException("The app isn't built.")) { UseShellExecute = false };
        start.Environment[AppPaths.HomeVariable] = Home;
        App = Application.Launch(start);
        Window = App.GetMainWindow(Automation, TimeSpan.FromSeconds(60)) ?? throw new InvalidOperationException("No main window.");
        Window.SetForeground();
        Thread.Sleep(1500); // the first listing, icons, the window settling at its size
    }

    // ---- finding things ----------------------------------------------------------------------------------

    /// <summary>The panel's file list (both are called FileList: told apart by their position).</summary>
    public AutomationElement List(bool left = true)
    {
        AutomationElement[] lists = Retry(() =>
        {
            AutomationElement[] found = Window.FindAllDescendants(cf => cf.ByAutomationId("FileList"));
            return found.Length >= 2 ? found : null;
        }, "the two file lists");
        lists = lists.OrderBy(l => l.BoundingRectangle.Left).ToArray();
        return left ? lists[0] : lists[^1];
    }

    /// <summary>The names of the items the list shows (the visible ones).</summary>
    public List<string> Items(bool left = true)
    {
        var names = new List<string>();
        // descendants: This PC puts its drives in groups
        foreach (AutomationElement item in List(left).FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
        {
            string name = item.Name;
            if (string.IsNullOrEmpty(name))
                name = item.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name ?? string.Empty;
            names.Add(name);
        }
        return names;
    }

    /// <summary>The focused item (the caret) of a panel.</summary>
    public string? Caret(bool left = true)
    {
        AutomationElement list = List(left);
        AutomationElement? selected = list.Patterns.Selection.PatternOrDefault?.Selection.Value.FirstOrDefault();
        return selected?.Name;
    }

    /// <summary>Waits until something is true (the app works asynchronously).</summary>
    public static void WaitUntil(Func<bool> condition, string what, double seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                if (condition())
                    return;
            }
            catch (Exception)
            {
                // the UI is changing under the question: ask again
            }
            Thread.Sleep(200);
        }
        Screenshot("FAILED - " + string.Concat(what.Split(Path.GetInvalidFileNameChars())));
        throw new TimeoutException($"Waited {seconds} s for: {what}");
    }

    private static T Retry<T>(Func<T?> get, string what, double seconds = 15) where T : class
    {
        T? value = null;
        WaitUntil(() => (value = get()) != null, what, seconds);
        return value!;
    }

    /// <summary>
    /// A window whose title starts with this (Find, Configuration): they belong to the main window, so UI Automation
    /// lists them under it (or on the desktop).
    /// </summary>
    public Window OtherWindow(string titleStart) => Retry(() =>
        Window.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Concat(Automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            .FirstOrDefault(w => (w.Name ?? string.Empty).StartsWith(titleStart, StringComparison.OrdinalIgnoreCase))?.AsWindow(), "the window " + titleStart);

    /// <summary>The information line of a panel (under its list).</summary>
    public string InfoLine(bool left = true)
    {
        AutomationElement[] lines = Window.FindAllDescendants(cf => cf.ByAutomationId("InfoText"))
            .OrderBy(l => l.BoundingRectangle.Left).ToArray();
        return lines.Length == 0 ? string.Empty : (left ? lines[0] : lines[^1]).Name ?? string.Empty;
    }

    // ---- the keyboard ------------------------------------------------------------------------------------

    public void Press(VirtualKeyShort key, int settleMs = 400)
    {
        Keyboard.Press(key);
        Keyboard.Release(key);
        Thread.Sleep(settleMs);
    }

    public void Press(VirtualKeyShort modifier, VirtualKeyShort key, int settleMs = 400)
    {
        Keyboard.TypeSimultaneously(modifier, key);
        Thread.Sleep(settleMs);
    }

    public void Type(string text, int settleMs = 400)
    {
        Keyboard.Type(text);
        Thread.Sleep(settleMs);
    }

    /// <summary>Puts the caret on an item by typing its start (quick search), then ends the quick search.</summary>
    public void GoTo(string name, bool left = true)
    {
        Press(VirtualKeyShort.HOME);
        Type(name[..Math.Min(name.Length, 6)]);
        WaitUntil(() => string.Equals(Caret(left), name, StringComparison.OrdinalIgnoreCase), "the caret on " + name, 5);
        Press(VirtualKeyShort.ESCAPE);
    }

    /// <summary>Opens a menu of the menu bar and chooses an item, through submenus ("Plugins", "Disk Map", "Show disk map").</summary>
    public void Menu(string menu, params string[] items)
    {
        AutomationElement bar = Retry(() => Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")), "the menu bar");
        AutomationElement top = Retry(() => bar.FindFirstDescendant(cf => cf.ByName(menu)), "the menu " + menu);
        top.Click();
        Thread.Sleep(600);
        foreach (string item in items)
        {
            AutomationElement entry = Retry(() => Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(item).And(cf.ByControlType(ControlType.MenuItem))),
                "the menu item " + item);
            entry.Click();
            Thread.Sleep(700);
        }
    }

    /// <summary>A button of the main window (in a dialog) with this text, once it's there.</summary>
    public AutomationElement Button(string name, double seconds = 10) => Retry(() =>
        Window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button))), "the button " + name, seconds);

    /// <summary>Waits for the button and clicks it.</summary>
    public void Click(string button, int settleMs = 600)
    {
        Button(button).Click();
        Thread.Sleep(settleMs);
    }

    /// <summary>Clicks something with this name anywhere on the desktop (a tab of a plugin window, a page of Configuration).</summary>
    public void ClickNamed(string name, int settleMs = 1200)
    {
        AutomationElement element = Retry(() => Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name)), "something named " + name);
        element.Click();
        Thread.Sleep(settleMs);
    }

    /// <summary>A text of the main window with exactly this content, or null.</summary>
    public AutomationElement? Text(string text) =>
        Window.FindFirstDescendant(cf => cf.ByName(text).And(cf.ByControlType(ControlType.Text)));

    /// <summary>The row of an item in a panel (it must be visible).</summary>
    public AutomationElement Row(string name, bool left = true) => Retry(() =>
        List(left).FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)), "the row " + name);

    /// <summary>Closes the app's other windows (Find, Configuration, plugin windows).</summary>
    public void CloseOtherWindows()
    {
        foreach (AutomationElement w in Window.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
        {
            try
            {
                w.AsWindow().Close();
            }
            catch (Exception)
            {
                // closed already
            }
        }
        Thread.Sleep(500);
        Window.SetForeground(); // the keys go to the main window again
        Thread.Sleep(300);
    }

    // ---- the mouse ---------------------------------------------------------------------------------------

    /// <summary>
    /// Drags with the mouse like a hand would: pressed, a few small moves to start the drag, across in steps, a moment
    /// over the target moving a little (the target follows the pointer), released. <paramref name="whileOver"/> runs
    /// while the pointer is over the target (a screenshot of the drag).
    /// </summary>
    public static void Drag(System.Drawing.Point from, System.Drawing.Point to, Action? whileOver = null)
    {
        double speed = Mouse.MovePixelsPerMillisecond;
        Mouse.MovePixelsPerMillisecond = 0.4; // FlaUI moves the pointer smoothly, like a hand
        try
        {
            Mouse.MoveTo(from);
            Thread.Sleep(300);
            Mouse.Down(MouseButton.Left);
            Thread.Sleep(300);
            Mouse.MoveTo(new System.Drawing.Point(from.X + 30, from.Y + 12)); // past the drag threshold
            Thread.Sleep(500);
            Mouse.MoveTo(to);
        }
        finally
        {
            Mouse.MovePixelsPerMillisecond = speed;
        }
        for (int i = 0; i < 6; i++)
        {
            Mouse.MoveTo(new System.Drawing.Point(to.X + (i % 2 == 0 ? 4 : -4), to.Y));
            Thread.Sleep(120);
        }
        whileOver?.Invoke();
        Thread.Sleep(400);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(1000);
    }

    /// <summary>A point on the name of a row (where a drag starts: the empty part of a row starts a selection rectangle).</summary>
    public System.Drawing.Point OnName(string name, bool left = true)
    {
        System.Drawing.Rectangle r = Row(name, left).BoundingRectangle;
        return new System.Drawing.Point(r.Left + 50, r.Top + r.Height / 2);
    }

    /// <summary>The empty part of a panel below its items.</summary>
    public System.Drawing.Point EmptyArea(bool left = true)
    {
        System.Drawing.Rectangle r = List(left).BoundingRectangle;
        return new System.Drawing.Point(r.Left + r.Width / 2, r.Bottom - 60);
    }

    public static void DoubleClick(System.Drawing.Point at)
    {
        Mouse.MoveTo(at);
        Thread.Sleep(150);
        Mouse.DoubleClick();
        Thread.Sleep(800);
    }

    public static void RightClick(System.Drawing.Point at)
    {
        Mouse.MoveTo(at);
        Thread.Sleep(150);
        Mouse.RightClick();
        Thread.Sleep(1000);
    }

    // ---- what to look at -----------------------------------------------------------------------------------

    /// <summary>
    /// A picture of the screen (the window with its menus and dialogs, and other windows of the app); without the
    /// taskbar when <paramref name="workArea"/> (pictures for the documentation).
    /// </summary>
    public static string Screenshot(string name, bool workArea = false)
    {
        Rectangle screen = workArea ? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea : System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(screen.Width, screen.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
        string path = Path.Combine(ScreenshotFolder, name + ".png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>The error log of this run ("" when nothing went wrong).</summary>
    public string ErrorLogText()
    {
        string logs = Path.Combine(Home, "Logs");
        return Directory.Exists(logs)
            ? string.Join("\n", Directory.EnumerateFiles(logs, "errors-*.log").Select(File.ReadAllText))
            : string.Empty;
    }

    public void Dispose()
    {
        try
        {
            if (!App.HasExited)
            {
                Window.Close();
                var clock = Stopwatch.StartNew();
                while (!App.HasExited && clock.Elapsed.TotalSeconds < 8)
                    Thread.Sleep(200);
            }
        }
        catch (Exception)
        {
            // closing is best effort: killed below
        }
        if (!App.HasExited)
            App.Kill();
        App.Dispose();
        Automation.Dispose();
        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A UI test: skipped when the app isn't built (a machine without the Release build, the quick CI check).</summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (AppSession.AppExe == null)
            Skip = "The app isn't built (set VIBE_APP_EXE or build Release x64).";
    }
}
