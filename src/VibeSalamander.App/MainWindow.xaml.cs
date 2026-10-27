// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VibeSalamander.Views;

namespace VibeSalamander;

public sealed partial class MainWindow : Window, IUiHost, ISettingsHost
{
    private readonly MainViewModel _vm;
    private bool _dialogOpen;
    private FindWindow? _findWindow;
    private DeviceWatcher? _deviceWatcher;
    private PluginUi? _pluginUi;

    /// <summary>Windows and dialogs of plugins (Calculate / Verify Checksums).</summary>
    public Plugins.IPluginUi PluginUi => _pluginUi ??= new PluginUi(DispatcherQueue, () => RootGrid.ActualTheme, Dialogs, WindowHandle);
    private bool _wasMinimized;

    public MainWindow()
    {
        InitializeComponent();
        ApplyMenuIcons(MainMenu.Items.OfType<MenuBarItem>().SelectMany(m => m.Items));

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon));
        }

        Dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme, () => AppWindow.Id);
        Dialogs.DialogOpenChanged += (_, open) =>
        {
            _dialogOpen = open;
            // modifier key-ups inside a dialog never reach RootGrid
            UpdateFunctionKeyBar(CurrentModifiers());
        };
        _vm = new MainViewModel(this, DispatcherQueue);
        UiState.ApplyOptions(_vm.Settings);
        UiState.ApplyDensity(_vm.Settings.Density);
        LeftPane.Initialize(_vm.Left, _vm);
        RightPane.Initialize(_vm.Right, _vm);
        LeftPane.TypedToCommandLine += (_, c) => TypeIntoCommandLine(c);
        RightPane.TypedToCommandLine += (_, c) => TypeIntoCommandLine(c);
        LeftPane.ApplyOptions();
        RightPane.ApplyOptions();
        ApplyThemeSetting();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.WindowTitle) or nameof(MainViewModel.Active))
                UpdateTitle();
        };
        _vm.Left.PropertyChanged += Pane_PropertyChanged;
        _vm.Right.PropertyChanged += Pane_PropertyChanged;
        _vm.CaretMoveRequested += (_, e) => PaneView(e.Pane).MoveCaret(e.Index);

        TransfersView.Initialize(_vm.Transfers);
        _vm.Transfers.StateChanged += (_, _) =>
        {
            UpdateTransfersLayout();
            ScheduleTransfersAutoClose();
        };
        TransfersView.CollapsedChanged += (_, _) => UpdateTransfersLayout();
        TransfersView.HoldChanged += (_, _) => ScheduleTransfersAutoClose(); // the mouse or focus on the panel holds the auto close
        RootGrid.SizeChanged += (_, _) => UpdateTransfersLayout(); // keep the panel within 60 % of the window
        TransfersView.Interacted += (_, _) => PaneView(_vm.Active).FocusList();
        AppWindow.Closing += AppWindow_Closing;
        Activated += (_, e) =>
        {
            _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            // a modifier released while another window had the focus never reports its key-up here
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                UpdateFunctionKeyBar(KeyModifiers.None);
            else
                RefreshOnActivation();
        };
        _vm.Transfers.Finished += (_, _) =>
        {
            // Configuration → General: a beep when work finishes while the window is in the background
            if (!_windowActive && _vm.Settings.General.BeepWhenDoneInBackground && _vm.Transfers.ActiveCount == 0)
                MessageBeep(0);
        };

        // minimized: the kept thumbnails are dropped and freed memory goes back to Windows
        AppWindow.Changed += (_, _) =>
        {
            bool minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (minimized && !_wasMinimized)
            {
                _vm.Thumbnails.Clear();
                MemoryCleanup.CollectNow();
            }
            _wasMinimized = minimized;
        };
        TaskbarIdentity.Apply(WindowHandle); // a release runs from "lib": pinning uses the starter in the main folder

        // live drives: USB drives and memory cards open when plugged in, panels leave drives that are pulled out
        _deviceWatcher = new DeviceWatcher(WindowHandle);
        _deviceWatcher.Arrived += async (_, roots) => await _vm.OnDrivesArrivedAsync(roots);
        _deviceWatcher.Removed += async (_, roots) => await _vm.OnDrivesRemovedAsync(roots);

        _vm.LoadPlugins();
        BuildPluginsMenu();
        BuildFunctionKeyBar();
        BuildToolbar();
        BuildQuickBars();
        UpdateMenuShortcuts();
        _vm.QuickBarsChanged += (_, _) => BuildQuickBars();
        RestoreWindowPlacement();
        Loc.Translate(RootGrid); // the fixed texts of the window (menus, bars, headers) in the chosen language
        ApplyBars();
        ApplyAlwaysOnTop(_vm.Settings.AlwaysOnTop);
        RootGrid.ActualThemeChanged += (_, _) => OnThemeChanged();
        Closed += (_, _) => OnClosed();
    }

    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private FilePane PaneView(PaneViewModel pane) => pane == _vm.Left ? LeftPane : RightPane;

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleBarButtons();
        await _vm.InitializeAsync();
        UpdateTitle();
    }

    private void Pane_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.Path) or nameof(PaneViewModel.IsActive))
            UpdateTitle();
        if (e.PropertyName is nameof(PaneViewModel.EffectiveViewMode))
            UpdateThumbnailSlider();
    }

    private void UpdateTitle()
    {
        // Configuration → Main Window: directory name only, shortened path or full path, with a prefix
        string path = _vm.Active.DisplayPath;
        if (_vm.Active.IsLocalDisk && path.Length > 3)
        {
            path = _vm.Settings.Window.TitleMode switch
            {
                TitleMode.DirectoryName => Path.GetFileName(path.TrimEnd('\\')),
                TitleMode.Shortened => ShortenPath(path),
                _ => path,
            };
        }
        string prefix = _vm.Settings.Window.TitlePrefix.Trim();
        if (prefix.Length > 0)
            path = string.IsNullOrEmpty(path) ? prefix : $"{prefix} {path}";
        TitleText.Text = string.IsNullOrEmpty(path) ? "Vibe Salamander" : $"{path} - Vibe Salamander";
        Title = TitleText.Text;
        CommandPrompt.Text = _vm.Active.IsLocalDisk ? _vm.Active.Path + ">" : string.Empty;
    }

    /// <summary>"C:\…\Projects\Vibe": the root and the last two directories.</summary>
    private static string ShortenPath(string path)
    {
        string[] parts = path.TrimEnd('\\').Split('\\');
        return parts.Length <= 4 ? path : $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}";
    }

    private bool _windowActive = true;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    /// <summary>Panels without auto refresh (drive options) update when the window becomes active, like Salamander.</summary>
    private void RefreshOnActivation()
    {
        foreach (PaneViewModel pane in new[] { _vm.Left, _vm.Right })
        {
            if (!pane.IsLocalDisk || pane.HasAutoRefresh)
                continue;
            if (pane.CurrentDriveType == DriveType.Network && _vm.Settings.Drives.NoRefreshOnActivation)
                continue;
            _ = pane.RefreshAsync();
        }
    }

    /// <summary>Configuration → Panels: typing in a panel goes to the command line.</summary>
    private void TypeIntoCommandLine(char c)
    {
        if (CommandLineBar.Visibility != Visibility.Visible)
            return;
        CommandLine.Text += c;
        CommandLine.Focus(FocusState.Programmatic);
        CommandLine.SelectionStart = CommandLine.Text.Length;
    }

    private int _historyIndex = -1;

    // ---- panels --------------------------------------------------------------------------------

    public void FocusPane(PaneViewModel pane) => PaneView(pane).FocusList();

    public void ShowDriveMenu(PaneViewModel pane) => PaneView(pane).ShowDriveMenu();

    public void SetPanelMaximized(PaneViewModel? pane)
    {
        if (pane == null)
        {
            ApplySplitRatio(_vm.Settings.SplitRatio);
            SplitterColumn.Width = new GridLength(6);
        }
        else
        {
            bool left = pane == _vm.Left;
            LeftColumn.Width = new GridLength(left ? 1 : 0, GridUnitType.Star);
            RightColumn.Width = new GridLength(left ? 0 : 1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
        }
    }

    private void ApplySplitRatio(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.1, 0.9);
        LeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private bool _splitting;

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _splitting = Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitting)
            return;
        double width = PanelsGrid.ActualWidth - PanelsGrid.Padding.Left - PanelsGrid.Padding.Right;
        if (width <= 0)
            return;
        double x = e.GetCurrentPoint(PanelsGrid).Position.X - PanelsGrid.Padding.Left;
        _vm.Settings.SplitRatio = Math.Clamp(x / width, 0.1, 0.9);
        ApplySplitRatio(_vm.Settings.SplitRatio);
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _splitting = false;
        Splitter.ReleasePointerCapture(e.Pointer);
    }

    private void Splitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _vm.Settings.SplitRatio = 0.5;
        ApplySplitRatio(0.5);
    }

    public void EditPath(PaneViewModel pane) => PaneView(pane).BeginPathEdit();

    public void ShowDriveInformation(PaneViewModel pane) => PaneView(pane).ShowDriveInformation();

    public void ShowMenu(PaneViewModel pane, IReadOnlyList<MenuEntry> entries, bool atCaret) => PaneView(pane).ShowMenu(entries, atCaret);

    public bool ShowNewMenu(PaneViewModel pane) => PaneView(pane).ShowNewMenu();

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
            await _vm.OpenSpecialFolderAsync(key);
    }

    public void ShowFindWindow(string directory)
    {
        if (_findWindow == null)
        {
            _findWindow = new FindWindow(directory, RootGrid.ActualTheme, _vm, FocusFoundItemAsync);
            WindowOwner.Set(_findWindow, WindowHandle); // stays in front of the main window while open
            _findWindow.Closed += (_, _) =>
            {
                _findWindow = null;
                MemoryCleanup.Schedule(); // the closed window and its results are let go
            };
        }
        else
        {
            _findWindow.SetLookIn(directory);
        }
        _findWindow.Activate();
    }

    /// <summary>
    /// "Focus in Panel" / "Open file location" from the Find window: shows the item in the active panel
    /// (name null: opens the directory or archive itself).
    /// </summary>
    private async Task FocusFoundItemAsync(string directory, string? name)
    {
        await _vm.Active.NavigateAsync(directory, name);
        Activate();
        PaneView(_vm.Active).FocusList();
    }

    public Task<string?> EditNameInPlaceAsync(PaneViewModel pane, FileItemViewModel item, bool selectWholeName = false) =>
        PaneView(pane).EditNameInPlaceAsync(item, selectWholeName);

    public void ShowContextMenu(PaneViewModel pane) => PaneView(pane).ShowContextMenuForCaret();

    // ---- window placement --------------------------------------------------------------------

    private void RestoreWindowPlacement()
    {
        AppSettings s = _vm.Settings;
        if (s.WindowX >= 0 && s.WindowY >= 0)
            AppWindow.MoveAndResize(new RectInt32(s.WindowX, s.WindowY, Math.Max(640, s.WindowWidth), Math.Max(400, s.WindowHeight)));
        else
            AppWindow.Resize(new SizeInt32(Math.Max(640, s.WindowWidth), Math.Max(400, s.WindowHeight)));
        if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        ApplySplitRatio(s.SplitRatio);
    }

    private void OnClosed()
    {
        _deviceWatcher?.Dispose();
        AppSettings s = _vm.Settings;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            s.WindowMaximized = presenter.State == OverlappedPresenterState.Maximized;
            if (presenter.State == OverlappedPresenterState.Restored)
            {
                s.WindowX = AppWindow.Position.X;
                s.WindowY = AppWindow.Position.Y;
                s.WindowWidth = AppWindow.Size.Width;
                s.WindowHeight = AppWindow.Size.Height;
            }
        }
        _findWindow?.Close();
        _settingsWindow?.Close();
        _vm.Transfers.CancelAll();
        _vm.SaveSettings(onExit: true);
        _pluginUi?.CloseAll();
        _vm.Plugins.ShutdownAll();
        MainViewModel.CleanArchiveTemp();
        _vm.Left.Dispose();
        _vm.Right.Dispose();
    }
}
