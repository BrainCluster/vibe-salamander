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
    public Window Window { get; private set; } = null!;

    /// <param name="prepareHome">Called with the settings folder before the app starts (to put a plugin into it).</param>
    /// <param name="arguments">The program's command line (-L folder, -R folder, -P 2).</param>
    public AppSession(Action<AppSettings> configure, Action<string>? prepareHome = null, IEnumerable<string>? arguments = null)
    {
        Home = Path.Combine(Path.GetTempPath(), "vibe-ui-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Home);
        prepareHome?.Invoke(Home);
        var settings = new AppSettings { WindowMaximized = true, ShowHidden = true };
        settings.General.Language = "en";
        settings.General.SingleInstance = false;
        settings.General.CheckForUpdates = false; // no calls to GitHub from the tests
        settings.ViewStyle = VibeSalamander.Core.Panels.ViewStyle.Classic; // the tests expect one view per panel (a test can choose Modern)
        configure(settings);
        SettingsStore.Save(settings, Path.Combine(Home, "settings.json"));

        var start = new ProcessStartInfo(AppExe ?? throw new InvalidOperationException("The app isn't built.")) { UseShellExecute = false };
        start.Environment[AppPaths.HomeVariable] = Home;
        foreach (string argument in arguments ?? Array.Empty<string>())
            start.ArgumentList.Add(argument);
        SinceLaunch = Stopwatch.StartNew();
        App = Application.Launch(start);
        try
        {
            Window = App.GetMainWindow(Automation, TimeSpan.FromSeconds(60)) ?? throw new InvalidOperationException("No main window.");
            Window.SetForeground();
            // the app says when it's ready, and the panels have their first listing
            WaitUntil(() => IsReady(State()) && Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)) != null,
                "the app to be ready after its start (it says: \"" + State() + "\")", 60);
            ReadyAfter = SinceLaunch.Elapsed;
            Timing.Add("Starting the app (until it is ready)", ReadyAfter);
            Timing.Pause(200); // the window settling at its size (icons and thumbnails are part of "ready")
        }
        catch (Exception)
        {
            try
            {
                App.Kill(); // a test that fails here has no session to dispose
            }
            catch (Exception)
            {
                // gone already
            }
            throw;
        }
    }

    /// <summary>Running since the app was started.</summary>
    public Stopwatch SinceLaunch { get; }

    /// <summary>How long the start took: until the app said "ready" with the panels listed.</summary>
    public TimeSpan ReadyAfter { get; }

    // ---- ready or busy -----------------------------------------------------------------------------------

    private AutomationElement? _state;

    /// <summary>
    /// What the app says it's doing: "ready" or "busy: panel, thumbnails, ..." (listing, calculating sizes, the editor
    /// starting, the viewer loading, a search or comparison running, thumbnails and icons loading, a plugin showing
    /// progress), then "; dialog" while a dialog is open and "; transfers" while transfers run. "" when it can't be
    /// asked just now.
    /// </summary>
    public string State()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _state ??= Window.FindFirstDescendant(cf => cf.ByAutomationId("AppState"));
                if (_state != null)
                    return _state.Name ?? string.Empty;
            }
            catch (Exception)
            {
                _state = null; // the element went stale: looked up again
            }
        }
        return string.Empty;
    }

    private static bool IsReady(string state) => state.StartsWith("ready", StringComparison.Ordinal);

    /// <summary>Waits until the app says it's ready (however slow the machine is); fails when it stays busy, with the app's error log.</summary>
    public void WaitReady(double seconds = 30)
    {
        try
        {
            WaitUntil(() => IsReady(State()), "the app to be ready", seconds);
        }
        catch (TimeoutException ex)
        {
            string log = ErrorLogText();
            throw new TimeoutException($"{ex.Message} (it says: \"{State()}\")" + (log.Length > 0 ? "\nError log:\n" + log : string.Empty), ex);
        }
    }

    /// <summary>
    /// Ready, and a moment for what was drawn to reach the screen (before a screenshot: thumbnails, a window's
    /// content, a theme).
    /// </summary>
    public void Calm(int milliseconds = 250)
    {
        WaitReady(60);
        Timing.Pause(milliseconds);
    }

    /// <summary>Waits until a dialog is open (it has finished opening), or until none is when <paramref name="open"/> is false.</summary>
    public void WaitDialog(bool open = true, double seconds = 15)
    {
        WaitUntil(() => State().Contains("; dialog", StringComparison.Ordinal) == open, open ? "a dialog" : "the dialog closed", seconds);
        if (open)
            Timing.Pause(150); // its content drawn
    }

    /// <summary>Waits until no transfer runs any more.</summary>
    public void WaitTransfers(double seconds = 60) =>
        WaitUntil(() => State() is { Length: > 0 } state && !state.Contains("; transfers", StringComparison.Ordinal), "the transfers finished", seconds);

    /// <summary>Waits until a transfer runs (it appears in the transfer panel).</summary>
    public void WaitTransferStarted(double seconds = 15) =>
        WaitUntil(() => State().Contains("; transfers", StringComparison.Ordinal), "a transfer running", seconds);

    /// <summary>Another window of the app (Find, Configuration, a plugin's), once it's there and the app is ready.</summary>
    public Window OpenedWindow(string titleStart)
    {
        Window window = OtherWindow(titleStart);
        Calm(250);
        return window;
    }

    /// <summary>
    /// Waits for something that only matters for a screenshot (a popup menu, a text being drawn): true when it came,
    /// false after the time without failing the test.
    /// </summary>
    public static bool Soon(Func<bool> condition, double seconds = 5)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                if (condition())
                {
                    Timing.Add("Waiting for a state", clock.Elapsed);
                    return true;
                }
            }
            catch (Exception)
            {
                // the UI is changing under the question: ask again
            }
            Thread.Sleep(100);
        }
        Timing.Add("Waiting for a state", clock.Elapsed);
        return false;
    }

    /// <summary>A dialog or another window of the app has opened (whichever the command shows), and the app is ready.</summary>
    public void WaitDialogOrWindow()
    {
        WaitUntil(() => State().Contains("; dialog", StringComparison.Ordinal) ||
                        Window.FindAllChildren(cf => cf.ByControlType(ControlType.Window)).Length > 0, "a dialog or a window");
        Calm(250);
    }

    /// <summary>A popup menu (a context menu, a menu of the menu bar) is open, and drawn.</summary>
    public void WaitMenu()
    {
        Soon(() => Automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Menu)) != null);
        Timing.Pause(150);
    }

    /// <summary>The name of an item is being edited in place (F2, a new folder): the focus is in a text box.</summary>
    public void WaitNameEditor()
    {
        Soon(() => Automation.FocusedElement() is { } focused &&
                   focused.ControlType == ControlType.Edit && focused.AutomationId != "CommandLine");
        Timing.Pause(150);
    }

    /// <summary>
    /// After a key or a click: the pause a user's eye would need, and then for as long as the app says it's busy
    /// (at most 5 s, without failing: a dialog may be waiting for the next key while something still runs).
    /// </summary>
    private void Settle(int milliseconds)
    {
        Timing.Pause(milliseconds);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 5 && State().StartsWith("busy", StringComparison.Ordinal))
            Thread.Sleep(50);
        Timing.Add("Waiting while the app is busy (after keys and clicks)", clock.Elapsed);
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
                {
                    Timing.Add("Waiting for a state", clock.Elapsed);
                    return;
                }
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

    public static T Retry<T>(Func<T?> get, string what, double seconds = 15) where T : class
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

    public void Press(VirtualKeyShort key, int settleMs = 250)
    {
        Keyboard.Press(key);
        Keyboard.Release(key);
        Settle(settleMs);
    }

    public void Press(VirtualKeyShort modifier, VirtualKeyShort key, int settleMs = 250)
    {
        Keyboard.TypeSimultaneously(modifier, key);
        Settle(settleMs);
    }

    public void Type(string text, int settleMs = 250)
    {
        Keyboard.Type(text);
        Settle(settleMs);
    }

    /// <summary>Puts the caret on an item by typing its start (quick search), then ends the quick search.</summary>
    public void GoTo(string name, bool left = true)
    {
        bool There() => string.Equals(Caret(left), name, StringComparison.OrdinalIgnoreCase);
        Press(VirtualKeyShort.HOME);
        Type(name[..Math.Min(name.Length, 6)]);
        if (!Soon(There, 3))
        {
            // the keys went elsewhere: on a desktop with other windows (a local run), Windows may not have let the
            // just-started program take the keyboard from the terminal; once more, with the panel in front
            Press(VirtualKeyShort.ESCAPE);
            Window.SetForeground();
            List(left).Focus();
            Press(VirtualKeyShort.HOME);
            Type(name[..Math.Min(name.Length, 6)]);
            WaitUntil(There, "the caret on " + name, 5);
        }
        Press(VirtualKeyShort.ESCAPE);
    }

    /// <summary>Opens a menu of the menu bar and chooses an item, through submenus ("Plugins", "Disk Map", "Show disk map").</summary>
    public void Menu(string menu, params string[] items)
    {
        AutomationElement bar = Retry(() => Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")), "the menu bar");
        AutomationElement top = Retry(() => bar.FindFirstDescendant(cf => cf.ByName(menu)), "the menu " + menu);
        top.Click();
        Timing.Pause(300); // the menu opens; its items are waited for by name
        for (int i = 0; i < items.Length; i++)
        {
            string item = items[i];
            AutomationElement entry = Retry(() => Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(item).And(cf.ByControlType(ControlType.MenuItem))),
                "the menu item " + item);
            entry.Click();
            Settle(300);
            // a click that lands while the menu is still opening is lost: the menu stays open, so click once more
            if (i == items.Length - 1 && StillThere(entry))
            {
                entry.Click();
                Settle(300);
            }
        }
    }

    private static bool StillThere(AutomationElement element)
    {
        try
        {
            return !element.IsOffscreen;
        }
        catch (Exception)
        {
            return false; // the menu closed: the element is gone
        }
    }

    /// <summary>A button of the main window (in a dialog) with this text, once it's there.</summary>
    public AutomationElement Button(string name, double seconds = 10) => Retry(() =>
        Window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button))), "the button " + name, seconds);

    /// <summary>Waits for the button and clicks it.</summary>
    public void Click(string button, int settleMs = 250)
    {
        Button(button).Click();
        Settle(settleMs);
    }

    /// <summary>Clicks something with this name anywhere on the desktop (a tab of a plugin window, a page of Configuration).</summary>
    public void ClickNamed(string name, int settleMs = 300)
    {
        AutomationElement element = Retry(() => Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name)), "something named " + name);
        try
        {
            // in a list that scrolls (the configuration's pages), out of sight until scrolled to
            element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            Timing.Pause(150);
        }
        catch (Exception)
        {
            // not scrollable: clicked where it is
        }
        element.Click();
        Settle(settleMs);
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
        Soon(() => Window.FindAllChildren(cf => cf.ByControlType(ControlType.Window)).Length == 0, 5);
        Window.SetForeground(); // the keys go to the main window again
        Timing.Pause(150);
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
            Timing.Pause(300);
            Mouse.Down(MouseButton.Left);
            Timing.Pause(300);
            Mouse.MoveTo(new System.Drawing.Point(from.X + 30, from.Y + 12)); // past the drag threshold
            Timing.Pause(500);
            Mouse.MoveTo(to);
        }
        finally
        {
            Mouse.MovePixelsPerMillisecond = speed;
        }
        for (int i = 0; i < 6; i++)
        {
            Mouse.MoveTo(new System.Drawing.Point(to.X + (i % 2 == 0 ? 4 : -4), to.Y));
            Timing.Pause(120);
        }
        whileOver?.Invoke();
        Timing.Pause(400);
        Mouse.Up(MouseButton.Left);
        Timing.Pause(1000);
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
        Timing.Pause(150);
        Mouse.DoubleClick();
        Timing.Pause(300);
    }

    public static void RightClick(System.Drawing.Point at)
    {
        Mouse.MoveTo(at);
        Timing.Pause(150);
        Mouse.RightClick();
        Timing.Pause(600); // the menu opens (a static helper: callers that need it wait with WaitMenu)
    }

    // ---- what to look at -----------------------------------------------------------------------------------

    /// <summary>
    /// A picture of the screen (the window with its menus and dialogs, and other windows of the app); without the
    /// taskbar when <paramref name="workArea"/> (pictures for the documentation).
    /// </summary>
    public static string Screenshot(string name, bool workArea = false)
    {
        Rectangle screen = workArea ? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea : System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var clock = Stopwatch.StartNew();
        using var bitmap = new Bitmap(screen.Width, screen.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
        string path = Path.Combine(ScreenshotFolder, name + ".png");
        bitmap.Save(path, ImageFormat.Png);
        Timing.Add("Screenshots", clock.Elapsed);
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
        var closing = Stopwatch.StartNew();
        try
        {
            DisposeCore();
        }
        finally
        {
            Timing.Add("Closing the app", closing.Elapsed);
            Timing.Write();
        }
    }

    private void DisposeCore()
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

/// <summary>
/// A UI test that needs real pointer moves (hover, drag and drop): on GitHub's runners simulated moves don't reach
/// WinUI, so it runs only on a real desktop (dotnet test locally).
/// </summary>
public sealed class PointerUiFactAttribute : FactAttribute
{
    public PointerUiFactAttribute()
    {
        if (AppSession.AppExe == null)
            Skip = "The app isn't built (set VIBE_APP_EXE or build Release x64).";
        else if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Skip = "Simulated pointer moves don't reach WinUI on GitHub's runners: run it locally.";
    }
}
