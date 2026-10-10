using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Settings;
using VibeSalamander.ViewModels;
using VibeSalamander.Views;

namespace VibeSalamander;

public sealed partial class MainWindow : Window, IUiHost
{
    private readonly MainViewModel _vm;
    private bool _dialogOpen;
    private FindWindow? _findWindow;

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon));
        }

        Dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme);
        Dialogs.DialogOpenChanged += (_, open) => _dialogOpen = open;
        _vm = new MainViewModel(this, DispatcherQueue);
        UiState.ApplyDensity(_vm.Settings.Density);
        LeftPane.Initialize(_vm.Left, _vm);
        RightPane.Initialize(_vm.Right, _vm);
        ApplyThemeSetting();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.WindowTitle) or nameof(MainViewModel.Active))
                UpdateTitle();
        };
        _vm.Left.PropertyChanged += Pane_PropertyChanged;
        _vm.Right.PropertyChanged += Pane_PropertyChanged;
        _vm.CaretMoveRequested += (_, e) => PaneView(e.Pane).MoveCaret(e.Index);

        BuildFunctionKeyBar();
        RestoreWindowPlacement();
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
    }

    private void UpdateTitle()
    {
        string path = _vm.Active.DisplayPath;
        TitleText.Text = string.IsNullOrEmpty(path) ? "Vibe Salamander" : $"{path} - Vibe Salamander";
        Title = TitleText.Text;
        CommandPrompt.Text = _vm.Active.IsLocalDisk ? _vm.Active.Path + ">" : string.Empty;
    }

    // ---- keyboard ----------------------------------------------------------------------------

    private static KeyModifiers CurrentModifiers() => Services.KeyboardState.CurrentModifiers();

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen || Content?.XamlRoot == null)
            return;
        if (Overlay.IsActive)
        {
            // a file operation is running: only Esc (cancel) is accepted
            if (e.Key == Windows.System.VirtualKey.Escape)
                Overlay.Cancel();
            e.Handled = true;
            return;
        }
        // menus, flyouts and dialogs handle their own keys
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count > 0)
            return;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or MenuBar or MenuBarItem)
            return;

        int key = (int)e.Key;
        KeyModifiers mods = CurrentModifiers();
        FilePane activeView = PaneView(_vm.Active);

        if (key == VK.Back && mods == KeyModifiers.None && activeView.QuickSearchBackspace())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _vm.Active.EndQuickSearch();
            e.Handled = true;
            return;
        }

        CommandId command = _vm.KeyMap.Resolve(key, mods);
        if (command == CommandId.None && key == VK.Tab && mods == KeyModifiers.Shift)
            command = CommandId.SwitchPanel;
        if (command == CommandId.None)
            return;

        e.Handled = true;
        _vm.Active.EndQuickSearch();
        await _vm.ExecuteAsync(command);
        UpdateMenuChecks();
    }

    private async void CommandLine_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            string text = CommandLine.Text;
            CommandLine.Text = string.Empty;
            await _vm.RunCommandLineAsync(text);
            PaneView(_vm.Active).FocusList();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CommandLine.Text = string.Empty;
            PaneView(_vm.Active).FocusList();
        }
    }

    // ---- menu, toolbar, F-key bar ------------------------------------------------------------

    private async void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out CommandId command))
            await _vm.ExecuteAsync(command);
        UpdateMenuChecks();
    }

    private void LeftMenu_Click(object sender, RoutedEventArgs e)
    {
        _vm.Activate(_vm.Left);
        Menu_Click(sender, e);
    }

    private void RightMenu_Click(object sender, RoutedEventArgs e)
    {
        _vm.Activate(_vm.Right);
        Menu_Click(sender, e);
    }

    private async void Toolbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out CommandId command))
        {
            await _vm.ExecuteAsync(command);
            UpdateMenuChecks();
            PaneView(_vm.Active).FocusList();
        }
    }

    private void BuildFunctionKeyBar()
    {
        (string key, string label, CommandId command)[] keys =
        {
            ("F1", "Help", CommandId.Help),
            ("F2", "Rename", CommandId.QuickRename),
            ("F3", "View", CommandId.View),
            ("F4", "Edit", CommandId.Edit),
            ("F5", "Copy", CommandId.Copy),
            ("F6", "Move", CommandId.Move),
            ("F7", "MakeDir", CommandId.CreateDirectory),
            ("F8", "Delete", CommandId.Delete),
            ("F9", "UsrMenu", CommandId.None),
            ("F10", "Menu", CommandId.None),
        };
        for (int i = 0; i < keys.Length; i++)
        {
            var (key, label, command) = keys[i];
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x80, 0x80, 0x80)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 1),
                MinWidth = 24,
                Child = new TextBlock { Text = key, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            });
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = panel,
                Style = Application.Current.Resources.TryGetValue("VibeFKeyButtonStyle", out object? style) ? style as Style : null,
                IsEnabled = command != CommandId.None || key == "F10",
            };
            if (key == "F10")
                button.Click += (_, _) => FocusMenu();
            else
                button.Click += async (_, _) => { await _vm.ExecuteAsync(command); PaneView(_vm.Active).FocusList(); };
            Grid.SetColumn(button, i);
            FKeyBar.Children.Add(button);
        }
    }

    private void FocusMenu()
    {
        if (MainMenu.Items.Count > 0)
            MainMenu.Items[0].Focus(FocusState.Keyboard);
    }

    private async void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        var lines = Enum.GetValues<CommandId>()
            .Select(c => (c, _vm.KeyMap.GetShortcutText(c)))
            .Where(t => t.Item2 != null)
            .Select(t => $"{t.Item2,-16} {t.c}");
        await ShowMessageAsync("Keyboard Shortcuts", string.Join("\n", lines));
    }

    // ---- options -----------------------------------------------------------------------------

    private void UpdateMenuChecks()
    {
        HiddenFilesMenuItem.IsChecked = _vm.Settings.ShowHidden;
        ComfortableMenuItem.IsChecked = _vm.Settings.Density == Density.Comfortable;
        AlwaysOnTopMenuItem.IsChecked = _vm.Settings.AlwaysOnTop;
        ThemeSystemItem.IsChecked = _vm.Settings.Theme == AppTheme.System;
        ThemeLightItem.IsChecked = _vm.Settings.Theme == AppTheme.Light;
        ThemeDarkItem.IsChecked = _vm.Settings.Theme == AppTheme.Dark;
        ShowToolbarItem.IsChecked = _vm.Settings.ShowTopToolbar;
        ShowDriveBarsItem.IsChecked = _vm.Settings.ShowDriveBars;
        ShowInfoLineItem.IsChecked = _vm.Settings.ShowInformationLine;
        ShowCommandLineItem.IsChecked = _vm.Settings.ShowCommandLine;
        ShowFKeyBarItem.IsChecked = _vm.Settings.ShowFunctionKeyBar;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out AppTheme theme))
        {
            _vm.Settings.Theme = theme;
            ApplyThemeSetting();
        }
    }

    private void ApplyThemeSetting()
    {
        RootGrid.RequestedTheme = _vm.Settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        OnThemeChanged();
    }

    private void OnThemeChanged()
    {
        UiState.ApplyTheme(RootGrid.ActualTheme == ElementTheme.Dark);
        UpdateTitleBarButtons();
        _vm.Left.Rebuild();
        _vm.Right.Rebuild();
        LeftPane.RefreshBindings();
        RightPane.RefreshBindings();
        UpdateMenuChecks();
    }

    private void UpdateTitleBarButtons()
    {
        var titleBar = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }

    public void ApplyDensity()
    {
        UiState.ApplyDensity(_vm.Settings.Density);
        _vm.Left.Rebuild();
        _vm.Right.Rebuild();
        UpdateMenuChecks();
    }

    public void ApplyAlwaysOnTop(bool onTop)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = onTop;
    }

    private void ShowBars_Click(object sender, RoutedEventArgs e)
    {
        AppSettings s = _vm.Settings;
        s.ShowTopToolbar = ShowToolbarItem.IsChecked;
        s.ShowDriveBars = ShowDriveBarsItem.IsChecked;
        s.ShowInformationLine = ShowInfoLineItem.IsChecked;
        s.ShowCommandLine = ShowCommandLineItem.IsChecked;
        s.ShowFunctionKeyBar = ShowFKeyBarItem.IsChecked;
        ApplyBars();
    }

    private void ApplyBars()
    {
        AppSettings s = _vm.Settings;
        TopToolbar.Visibility = s.ShowTopToolbar ? Visibility.Visible : Visibility.Collapsed;
        CommandLineBar.Visibility = s.ShowCommandLine ? Visibility.Visible : Visibility.Collapsed;
        FKeyBar.Visibility = s.ShowFunctionKeyBar ? Visibility.Visible : Visibility.Collapsed;
        LeftPane.SetBarsVisibility(s.ShowDriveBars, s.ShowInformationLine);
        RightPane.SetBarsVisibility(s.ShowDriveBars, s.ShowInformationLine);
        UpdateMenuChecks();
    }

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

    // ---- dialogs -----------------------------------------------------------------------------

    public async Task<string?> PromptAsync(string title, string label, string initialText, string primaryButton, bool selectNameWithoutExtension = false)
    {
        var box = new TextBox { Text = initialText, MinWidth = 420 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
        };
        bool accepted = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                accepted = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            int dot = box.Text.LastIndexOf('.');
            if (selectNameWithoutExtension && dot > 0)
                box.Select(0, dot);
            else
                box.SelectAll();
        };

        _dialogOpen = true;
        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            accepted |= result == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
            PaneView(_vm.Active).FocusList();
        }
        return accepted ? box.Text : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryButton)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
        };
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
            PaneView(_vm.Active).FocusList();
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono,Consolas") },
                MaxHeight = 480,
            },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
        };
        _dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    public async void ShowAbout()
    {
        await ShowMessageAsync("About Vibe Salamander",
            "Vibe Salamander 0.1 (phase 1)\n\n" +
            "A modern spiritual successor to Altap / Open Salamander:\n" +
            "dual-pane, keyboard-first, fast and precise, built with C# and WinUI 3.\n\n" +
            "Settings: " + SettingsStore.FilePath);
    }

    public void Exit() => Close();

    // ---- phase 2 host services -----------------------------------------------------------------

    public Dialogs Dialogs { get; }

    public CancellationToken BeginOperation(string title) => Overlay.Begin(title);

    public void UpdateOperation(Core.Operations.OperationProgress progress) => Overlay.Update(progress);

    public void EndOperation()
    {
        Overlay.End();
        PaneView(_vm.Active).FocusList();
    }

    public void ShowFindWindow(string directory)
    {
        if (_findWindow == null)
        {
            _findWindow = new FindWindow(directory, RootGrid.ActualTheme, FocusFoundItemAsync);
            _findWindow.Closed += (_, _) => _findWindow = null;
        }
        else
        {
            _findWindow.SetLookIn(directory);
        }
        _findWindow.Activate();
    }

    /// <summary>"Focus in Panel" from the Find window: show the item in the active panel.</summary>
    private async Task FocusFoundItemAsync(string directory, string name)
    {
        await _vm.Active.NavigateAsync(directory, name);
        Activate();
        PaneView(_vm.Active).FocusList();
    }

    public Task<string?> EditNameInPlaceAsync(PaneViewModel pane, FileItemViewModel item) =>
        PaneView(pane).EditNameInPlaceAsync(item);

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
        _vm.SaveSettings();
        _vm.Left.Dispose();
        _vm.Right.Dispose();
    }
}
