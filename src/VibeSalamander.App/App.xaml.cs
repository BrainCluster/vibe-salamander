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
        // Configuration → General → "Allow only one instance": a second start activates the running window
        if (SettingsStore.Load().General?.SingleInstance == true)
        {
            AppInstance main = AppInstance.FindOrRegisterForKey("VibeSalamander.Main");
            if (!main.IsCurrent)
            {
                AllowSetForegroundWindow(main.ProcessId);
                await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            // Activated is raised on a background thread: go through the UI thread's queue (captured here)
            Microsoft.UI.Dispatching.DispatcherQueue ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            main.Activated += (_, _) => ui.TryEnqueue(() => _window?.BringToFront());
        }
        _window = new MainWindow();
        _window.Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
