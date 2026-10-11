using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using VibeSalamander.Core.Settings;

namespace VibeSalamander;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
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
                main.Activated += (_, _) => ui.TryEnqueue(() => _window?.BringToFront());
            else
                _ = RegisterWhenOldInstanceExitsAsync(ui); // restarting: take over the key once the old instance is gone
        }
        _window = new MainWindow();
        _window.Activate();
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
                main.Activated += (_, _) => ui.TryEnqueue(() => _window?.BringToFront());
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
