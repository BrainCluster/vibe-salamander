// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace VibeSalamander.UiTests;

/// <summary>The plugin contracts of 1.0 through the example plugin (samples/HelloPlugin): a viewer and a settings page.</summary>
public sealed class PluginContractScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    private AppSession? Start()
    {
        string? dll = Environment.GetEnvironmentVariable("VIBE_HELLO_PLUGIN");
        if (dll is null || !File.Exists(dll))
            return null; // not built on this machine
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
        }, prepareHome: home =>
        {
            string folder = Directory.CreateDirectory(Path.Combine(home, "Plugins", "HelloPlugin")).FullName;
            File.Copy(dll, Path.Combine(folder, "HelloPlugin.dll"));
        });
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        return app;
    }

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    [UiFact]
    public void A_plugin_viewer_shows_a_csv_file()
    {
        File.WriteAllText(Path.Combine(_files.Documents, "prices.csv"), "name,price\napple,1.20\nwatermelon,4\n");
        using AppSession? app = Start();
        if (app == null)
            return;
        AppSession.WaitUntil(() => app.Items().Contains("prices.csv"), "prices.csv");
        app.GoTo("prices.csv");
        app.Press(VirtualKeyShort.F3, settleMs: 400);
        Window viewer = app.OtherWindow("prices.csv (columns)");
        app.Calm(400);
        AppSession.Screenshot("85 plugin viewer");
        string shown = Directory.GetFiles(Path.Combine(Path.GetTempPath(), "VibeSalamander", "PluginViews"), "prices.csv (columns).txt", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).Select(File.ReadAllText).First();
        Assert.Contains("watermelon | 4", shown);
        Assert.Contains("apple      | 1.20", shown);
        viewer.Close();
        NoErrors(app);
    }

    [UiFact]
    public void A_plugin_has_a_settings_page()
    {
        using AppSession? app = Start();
        if (app == null)
            return;
        app.Menu("Options", "Configuration...");
        Window configuration = app.OpenedWindow("Configuration");
        app.ClickNamed("Plugins");
        // the Settings... button on Hello's card (built-in plugins with settings come before it)
        AutomationElement settings = AppSession.Retry(() =>
        {
            AutomationElement? name = configuration.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith("Hello", StringComparison.Ordinal)); // "Hello  1.0"
            var walker = app.Automation.TreeWalkerFactory.GetControlViewWalker();
            for (AutomationElement? card = name; card != null; card = walker.GetParent(card))
                if (card.FindFirstDescendant(cf => cf.ByName("Settings...").And(cf.ByControlType(ControlType.Button))) is { } button)
                    return button;
            return null;
        }, "the Settings... button of Hello");
        settings.AsButton().Invoke(); // the card may be below the visible part of the page
        AutomationElement field = AppSession.Retry(() => configuration.FindFirstDescendant(cf =>
            cf.ByName("How many files Largest files lists").And(cf.ByControlType(ControlType.Edit))), "the plugin's field");
        AppSession.Screenshot("86 plugin settings");
        field.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("7");
        Keyboard.Type(VirtualKeyShort.ENTER);
        string file = Path.Combine(app.Home, "PluginData", "example.hello", "settings.txt");
        AppSession.WaitUntil(() => File.Exists(file) && File.ReadAllText(file) == "7", "the plugin's saved setting");
        configuration.Close();
        NoErrors(app);
    }
}
