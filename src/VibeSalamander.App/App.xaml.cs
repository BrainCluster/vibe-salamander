// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using VibeSalamander.Core.Diagnostics;
using VibeSalamander.Core.Settings;

namespace VibeSalamander;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        string version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
        ErrorLog.Start(version);
        // an error nothing caught in the window (usually in an event handler): logged and reported, the program goes on
        UnhandledException += App_UnhandledException;
        // one on another thread ends the program: at least the log knows why, and the next start says so
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Crash("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Exception("Background task", e.Exception);
            e.SetObserved();
        };
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        ErrorLog.Exception("Unhandled exception", e.Exception ?? new InvalidOperationException(e.Message));
        e.Handled = true;
        string message = e.Exception?.Message is { Length: > 0 } m ? m : e.Message;
        _window?.Notify(Services.Loc.F("Something went wrong: {0} The details are in the error log (Help → Error Log).", message), isError: true, log: false);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppSettings settings = SettingsStore.Load();
        // Configuration → General → Language (before any window is built)
        Services.Loc.Load(settings.General?.Language);

        // Configuration → General → "Allow only one instance": a second start activates the running window
        // (not when Vibe Salamander restarts itself: the old window is still closing)
        bool restarting = Environment.GetCommandLineArgs().Contains("--restart", StringComparer.OrdinalIgnoreCase);
        if (settings.General?.SingleInstance == true)
        {
            AppInstance main = AppInstance.FindOrRegisterForKey("VibeSalamander.Main");
            if (!main.IsCurrent && !restarting)
            {
                AllowSetForegroundWindow(main.ProcessId);
                await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            // Activated is raised on a background thread: go through the UI thread's queue (captured here)
            Microsoft.UI.Dispatching.DispatcherQueue ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (main.IsCurrent)
                main.Activated += (_, e) => ui.TryEnqueue(() => Reactivated(e));
            else
                _ = RegisterWhenOldInstanceExitsAsync(ui); // restarting: take over the key once the old instance is gone
        }
        _window = new MainWindow();
        // -L / -R / -A folders, -P 1 / -P 2 (applied once the panels have loaded)
        _window.StartupArguments = StartupArguments.Parse(Environment.GetCommandLineArgs().Skip(1).Select(FullPath).ToList());
        _window.Activate();
        if (_window.SettingsProblem != null)
            _window.Notify("Some settings couldn't be read and are back to their defaults. The details are in the error log (Help → Error Log).",
                isError: true, log: false);
        else if (ErrorLog.TakeCrashNotice())
            _window.Notify("Vibe Salamander closed because of an error the last time it ran. The details are in the error log (Help → Error Log).",
                isError: false);
    }

    /// <summary>A second start (with one instance only): the window comes to the front, with that start's folders.</summary>
    private void Reactivated(AppActivationArguments e)
    {
        _window?.BringToFront();
        if (_window != null && e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            // the command line starts with the program itself
            List<string> parts = StartupArguments.Split(launch.Arguments ?? string.Empty).ToList();
            if (parts.Count > 0 && parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                parts.RemoveAt(0);
            StartupArguments arguments = StartupArguments.Parse(parts);
            if (!arguments.IsEmpty)
                _ = _window.ApplyArgumentsAsync(arguments);
        }
    }

    /// <summary>A relative folder given on the command line is relative to where the program was started from.</summary>
    private static string FullPath(string arg)
    {
        if (arg.StartsWith('-') || arg.StartsWith('/') || arg.Length == 0)
            return arg;
        try
        {
            return Path.IsPathRooted(arg) ? arg : Path.GetFullPath(arg.Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return arg;
        }
    }

    /// <summary>After a restart: registers as the single instance when the closing one has exited.</summary>
    private async Task RegisterWhenOldInstanceExitsAsync(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            await Task.Delay(200);
            AppInstance main = AppInstance.FindOrRegisterForKey("VibeSalamander.Main");
            if (main.IsCurrent)
            {
                main.Activated += (_, e) => ui.TryEnqueue(() => Reactivated(e));
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
