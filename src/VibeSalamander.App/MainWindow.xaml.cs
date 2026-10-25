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

    // ---- keyboard ----------------------------------------------------------------------------

    private static KeyModifiers CurrentModifiers() => Services.KeyboardState.CurrentModifiers();

    private static bool IsModifierKey(Windows.System.VirtualKey key) => key is
        Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl or
        Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu or
        Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift;

    private void RootGrid_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (IsModifierKey(e.Key))
            UpdateFunctionKeyBar(CurrentModifiers());
    }

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // the bottom bar shows what the F-keys do with the modifiers held right now
        if (IsModifierKey(e.Key))
        {
            UpdateFunctionKeyBar(CurrentModifiers());
            return;
        }
        if (_dialogOpen || Content?.XamlRoot == null)
            return;
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
        // Configuration → Panels: Shift+1…9 goes to the hot paths too
        if (command == CommandId.None && mods == KeyModifiers.Shift && key >= VK.D0 + 1 && key <= VK.D0 + 9 && _vm.Settings.Panels.ShiftDigitGoesToHotPath)
            command = CommandId.GoToHotPath0 + (key - VK.D0);
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
            _historyIndex = -1;
            await _vm.RunCommandLineAsync(text);
            PaneView(_vm.Active).FocusList();
        }
        else if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down && _vm.Settings.History.CommandLineHistory)
        {
            // Configuration → History: Up / Down recall earlier commands
            List<string> history = _vm.Settings.CommandLineHistory;
            if (history.Count == 0)
                return;
            e.Handled = true;
            _historyIndex = e.Key == Windows.System.VirtualKey.Up
                ? Math.Min(_historyIndex + 1, history.Count - 1)
                : Math.Max(_historyIndex - 1, -1);
            CommandLine.Text = _historyIndex >= 0 ? history[_historyIndex] : string.Empty;
            CommandLine.SelectionStart = CommandLine.Text.Length;
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

    // ---- top toolbar, hot path bar, user menu bar (phase 3C: built from the settings) ---------

    private Style? ToolButtonStyle =>
        Application.Current.Resources.TryGetValue("VibeToolButtonStyle", out object? style) ? style as Style : null;

    private FontFamily IconFont =>
        Application.Current.Resources.TryGetValue("VibeIconFont", out object? font) && font is FontFamily f ? f : new FontFamily("Segoe Fluent Icons");

    private string WithShortcut(string text, CommandId command) =>
        _vm.KeyMap.GetShortcutText(command) is string keys ? $"{text} ({keys})" : text;

    /// <summary>The top toolbar from Settings.Toolbar (command names, "-" separators, "ViewMenu").</summary>
    private void BuildToolbar()
    {
        ToolbarPanel.Children.Clear();
        bool sliderAdded = false;
        foreach (string id in _vm.Settings.Toolbar)
        {
            if (id == CommandCatalog.SeparatorId)
            {
                ToolbarPanel.Children.Add(new AppBarSeparator { Padding = new Thickness(2, 0, 2, 0) });
                continue;
            }
            if (id == CommandCatalog.ViewMenuId)
            {
                ToolbarPanel.Children.Add(BuildViewMenuButton());
                continue;
            }
            if (!Enum.TryParse(id, out CommandId command) || command == CommandId.None)
                continue;
            var button = new Button
            {
                Style = ToolButtonStyle,
                Tag = id,
                Content = new FontIcon { Glyph = CommandCatalog.Glyph(command), FontFamily = IconFont, FontSize = 15 },
            };
            ToolTipService.SetToolTip(button, WithShortcut(CommandCatalog.DisplayName(command), command));
            button.Click += Toolbar_Click;
            ToolbarPanel.Children.Add(button);
            if (command == CommandId.ToggleDensity && !sliderAdded)
            {
                ToolbarPanel.Children.Add(BuildThumbnailSlider());
                sliderAdded = true;
            }
        }
        // one slider for both panels: next to the density button, or at the end when that isn't on the toolbar
        if (!sliderAdded)
            ToolbarPanel.Children.Add(BuildThumbnailSlider());
        Loc.Translate(ToolbarPanel);
        UpdateThumbnailSlider();
    }

    // ---- the thumbnail size slider (toolbar and Configuration → Appearance) --------------------------------

    private Slider? _thumbnailSlider;
    private bool _syncingThumbnailSlider;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _thumbnailSaveTimer;

    /// <summary>The thumbnail size of both panels; on the toolbar while either panel shows thumbnails.</summary>
    private Slider BuildThumbnailSlider()
    {
        Slider slider = ThumbnailSlider.Create(150, size =>
        {
            if (!_syncingThumbnailSlider)
                SetThumbnailSize(size);
        });
        slider.Margin = new Thickness(8, 0, 8, 0);
        slider.IsTabStop = false;
        ToolTipService.SetToolTip(slider, "Thumbnail size");
        _thumbnailSlider = slider;
        return slider;
    }

    private void UpdateThumbnailSlider()
    {
        if (_thumbnailSlider == null)
            return;
        bool thumbnails = _vm.Left.EffectiveViewMode == ViewMode.Thumbnails || _vm.Right.EffectiveViewMode == ViewMode.Thumbnails;
        _thumbnailSlider.Visibility = thumbnails ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetThumbnailSize(int size)
    {
        size = Math.Clamp(size, UiState.MinThumbnailSize, UiState.MaxThumbnailSize);
        if (_vm.Settings.Appearance.ThumbnailSize == size)
            return;
        _vm.Settings.Appearance.ThumbnailSize = size;
        if (_thumbnailSlider != null && (int)Math.Round(_thumbnailSlider.Value) != size)
        {
            _syncingThumbnailSlider = true;
            _thumbnailSlider.Value = size;
            _syncingThumbnailSlider = false;
        }
        LeftPane.ApplyThumbnailSize();
        RightPane.ApplyThumbnailSize();
        // once the slider stops: sharper pictures for larger sizes, and the setting saved
        if (_thumbnailSaveTimer == null)
        {
            _thumbnailSaveTimer = DispatcherQueue.CreateTimer();
            _thumbnailSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _thumbnailSaveTimer.IsRepeating = false;
            _thumbnailSaveTimer.Tick += (_, _) =>
            {
                LeftPane.ReloadThumbnails();
                RightPane.ReloadThumbnails();
                _vm.SaveSettings();
            };
        }
        _thumbnailSaveTimer.Stop();
        _thumbnailSaveTimer.Start();
    }

    private Button BuildViewMenuButton()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var (text, command, glyph) in new[]
                 {
                     ("Brief", CommandId.ViewBrief, "\uE8A9"), ("Detailed", CommandId.ViewDetailed, "\uE8FD"),
                     ("Icons", CommandId.ViewIcons, "\uE80A"), ("Thumbnails", CommandId.ViewThumbnails, "\uE91B"),
                     ("Tiles", CommandId.ViewTiles, "\uECA5"),
                 })
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Tag = command.ToString(),
                Icon = new FontIcon { Glyph = glyph },
                KeyboardAcceleratorTextOverride = _vm.KeyMap.GetShortcutText(command) ?? string.Empty,
            };
            item.Click += Toolbar_Click;
            menu.Items.Add(item);
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(new FontIcon { Glyph = "\uE8FD", FontFamily = IconFont, FontSize = 15 });
        panel.Children.Add(new FontIcon { Glyph = "\uE70D", FontFamily = IconFont, FontSize = 8 });
        var button = new Button { Style = ToolButtonStyle, Content = panel, Flyout = menu };
        ToolTipService.SetToolTip(button, "View of the active panel (Alt+1…5)");
        return button;
    }

    /// <summary>Hot path bar (set hot paths) and user menu bar (user menu commands).</summary>
    private void BuildQuickBars()
    {
        HotPathBar.Children.Clear();
        var hotPaths = _vm.Settings.HotPaths;
        for (int i = 1; i < hotPaths.Count && i < 10; i++)
        {
            string path = hotPaths[i];
            if (string.IsNullOrEmpty(path))
                continue;
            string custom = i < _vm.Settings.HotPathNames.Count ? _vm.Settings.HotPathNames[i] : string.Empty;
            string label = !string.IsNullOrWhiteSpace(custom) ? custom
                : path == Core.FileSystem.ThisPcProvider.RootPath ? Loc.T("This PC")
                : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = "\uE734", FontFamily = IconFont, FontSize = 12 });
            content.Children.Add(new TextBlock { Text = $"{i} {label}", FontSize = 12 });
            var button = new Button { Style = ToolButtonStyle, Content = content, Tag = (CommandId.GoToHotPath0 + i).ToString() };
            ToolTipService.SetToolTip(button, $"{path}  (Ctrl+{i})");
            button.Click += Toolbar_Click;
            HotPathBar.Children.Add(button);
        }

        UserMenuBar.Children.Clear();
        foreach (Core.Tools.UserMenuItem item in _vm.Settings.UserMenu)
        {
            if (item.IsSeparator)
            {
                UserMenuBar.Children.Add(new AppBarSeparator { Padding = new Thickness(2, 0, 2, 0) });
                continue;
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = "\uE756", FontFamily = IconFont, FontSize = 12 });
            content.Children.Add(new TextBlock { Text = item.Name, FontSize = 12 });
            var button = new Button { Style = ToolButtonStyle, Content = content };
            ToolTipService.SetToolTip(button, $"{item.Command} {item.Arguments}".Trim());
            Core.Tools.UserMenuItem command = item;
            button.Click += (_, _) =>
            {
                _vm.RunUserMenuItem(command);
                PaneView(_vm.Active).FocusList();
            };
            UserMenuBar.Children.Add(button);
        }
        ApplyBars();
    }

    /// <summary>Menu shortcut texts follow the (customizable) key map.</summary>
    private void UpdateMenuShortcuts()
    {
        void Walk(IList<MenuFlyoutItemBase> items)
        {
            foreach (MenuFlyoutItemBase item in items)
            {
                if (item is MenuFlyoutSubItem sub)
                    Walk(sub.Items);
                else if (item is MenuFlyoutItem { Tag: string tag } menuItem && Enum.TryParse(tag, out CommandId command) &&
                         command != CommandId.None && !int.TryParse(tag, out _))
                    menuItem.KeyboardAcceleratorTextOverride = _vm.KeyMap.GetShortcutText(command) ??
                        (command == CommandId.Exit ? "Alt+F4" : string.Empty); // Alt+F4 is the system's, not in the key map
            }
        }
        foreach (MenuBarItem top in MainMenu.Items)
            Walk(top.Items);
    }

    // ---- configuration (phase 3C) --------------------------------------------------------------

    private SettingsWindow? _settingsWindow;

    public MainViewModel ViewModel => _vm;

    public void ShowConfiguration() => ShowConfiguration(null);

    public void ShowConfigurationPage(string page) => ShowConfiguration(page);

    /// <summary>
    /// Plugins menu, like Salamander's: the Plugins Manager, then one submenu per plugin with what it
    /// adds (Pack / Unpack for archivers, its own commands).
    /// </summary>
    private void BuildPluginsMenu()
    {
        PluginsMenu.Items.Clear();
        PluginsMenu.Items.Add(MenuItem("Plugins Manager...", nameof(CommandId.PluginsManager), "\uEA86"));
        var loaded = _vm.Plugins.Plugins.Where(p => p.Enabled && p.Error == null).ToList();
        if (loaded.Count > 0)
            PluginsMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (Services.PluginInfo plugin in loaded)
        {
            string pluginGlyph = PluginGlyph(plugin);
            var sub = new MenuFlyoutSubItem { Text = plugin.Name, Icon = new FontIcon { Glyph = pluginGlyph } };
            if (plugin.Archivers.Any(a => a.PackFormats.Count > 0))
                sub.Items.Add(MenuItem("Pack...", nameof(CommandId.Pack), "\uE7B8"));
            if (plugin.Archivers.Any(a => a.UnpackExtensions.Count > 0))
                sub.Items.Add(MenuItem("Unpack...", nameof(CommandId.Unpack), "\uE896"));
            foreach (Plugins.PluginCommand command in plugin.CommandContributors.SelectMany(c => c.Commands))
            {
                if (command.SeparatorBefore && sub.Items.Count > 0 && sub.Items[^1] is not MenuFlyoutSeparator)
                    sub.Items.Add(new MenuFlyoutSeparator());
                CommandId hostCommand = CommandId.None;
                bool isHostCommand = command.HostCommand != null && Enum.TryParse(command.HostCommand, out hostCommand);
                string glyph = isHostCommand && CommandCatalog.Glyph(hostCommand) is { Length: > 0 } commandGlyph ? commandGlyph : pluginGlyph;
                var item = new MenuFlyoutItem { Text = command.MenuText, Icon = new FontIcon { Glyph = glyph } };
                // a command answering to an app command shows that command's (customizable) key
                string? keys = isHostCommand ? _vm.KeyMap.GetShortcutText(hostCommand) : command.ShortcutText;
                if (!string.IsNullOrEmpty(keys))
                    item.KeyboardAcceleratorTextOverride = keys;
                Plugins.PluginCommand c = command;
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await c.Execute(plugin.Host!);
                    }
                    catch (Exception ex)
                    {
                        Notify($"{plugin.Name}: {ex.Message}");
                    }
                };
                sub.Items.Add(item);
            }
            if (sub.Items.Count == 0)
                sub.Items.Add(new MenuFlyoutItem { Text = "(nothing to show)", IsEnabled = false });
            PluginsMenu.Items.Add(sub);
        }
        foreach (MenuFlyoutItemBase item in PluginsMenu.Items)
            Loc.Translate(item);
        UpdateMenuShortcuts();
    }

    /// <summary>
    /// Icons of the menu items that aren't app commands (by tag), and of submenus and other items (by their
    /// English text, before the menus are translated).
    /// </summary>
    private static readonly Dictionary<string, string> TagGlyphs = new()
    {
        // Commands → Open Folder
        ["Desktop"] = "\uE8B7", ["Documents"] = "\uE8A5", ["Downloads"] = "\uE896", ["Pictures"] = "\uE91B",
        ["Music"] = "\uE8D6", ["Videos"] = "\uE714", ["UserProfile"] = "\uE77B", ["Computer"] = "\uE7F4",
        ["Temp"] = "\uE81C", ["Recent"] = "\uE81C", ["Templates"] = "\uE8A5", ["SendTo"] = "\uE724",
        ["StartMenu"] = "\uE71D", ["Programs"] = "\uE71D", ["Startup"] = "\uE768", ["ControlPanel"] = "\uE713",
        ["Network"] = "\uE968", ["RecycleBin"] = "\uE74D", ["Fonts"] = "\uE8D2", ["Printers"] = "\uE749",
        // Options (Theme and Show have check marks instead)
        ["Toolbar"] = "\uE771", ["Keyboard"] = "\uE765",
    };

    private static readonly Dictionary<string, string> TextGlyphs = new()
    {
        ["Save Configuration"] = "\uE74E", ["Export Configuration..."] = "\uEDE1", ["Import Configuration..."] = "\uE8B5",
        ["Keyboard Shortcuts"] = "\uE765",
        // submenus
        ["View"] = "\uE8A9", ["Sort By"] = "\uE8CB", ["NTFS"] = "\uEDA2", ["Advanced"] = "\uE712",
        ["Open Folder"] = "\uE838", ["Theme"] = "\uE790", ["Show"] = "\uE7B3",
    };

    /// <summary>Puts an icon before every menu item (and submenu) that has none.</summary>
    private static void ApplyMenuIcons(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (MenuFlyoutItemBase entry in items)
        {
            switch (entry)
            {
                case MenuFlyoutSubItem sub:
                    if (sub.Icon == null && TextGlyphs.TryGetValue(sub.Text, out string? subGlyph))
                        sub.Icon = new FontIcon { Glyph = subGlyph };
                    ApplyMenuIcons(sub.Items);
                    break;
                case MenuFlyoutItem item when item.Icon == null:
                    string? glyph = null;
                    if (item.Tag is string tag && TagGlyphs.TryGetValue(tag, out string? byTag))
                        glyph = byTag;
                    else if (item.Tag is string command && Enum.TryParse(command, out CommandId id) && id != CommandId.None)
                        glyph = CommandCatalog.Glyph(id);
                    else if (TextGlyphs.TryGetValue(item.Text, out string? byText))
                        glyph = byText;
                    if (!string.IsNullOrEmpty(glyph))
                        item.Icon = new FontIcon { Glyph = glyph };
                    break;
            }
        }
    }

    /// <summary>A plugin's icon in the Plugins menu: its own, or one for what it adds.</summary>
    private static string PluginGlyph(Services.PluginInfo plugin)
    {
        if (plugin.IconGlyph is { Length: > 0 } own)
            return own;
        if (plugin.Archivers.Count > 0)
            return "\uF012"; // a zipped folder
        if (plugin.FileSystems.Count > 0)
            return "\uE968"; // network
        return "\uEA86"; // a puzzle piece
    }

    private MenuFlyoutItem MenuItem(string text, string tag, string glyph)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = tag, Icon = new FontIcon { Glyph = glyph } };
        item.Click += Menu_Click;
        return item;
    }

    private void ShowConfiguration(string? page)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this, RootGrid.ActualTheme, page);
            _settingsWindow.SetOwner(WindowHandle); // stays in front of the main window while open
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _vm.SaveSettings();
                MemoryCleanup.Schedule(); // the closed window's controls and pages are let go
            };
        }
        else if (page != null)
        {
            _settingsWindow.ShowPage(page);
        }
        _settingsWindow.Activate();
    }

    private void ConfigurationPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
            ShowConfiguration(page);
    }

    /// <summary>Everything the settings window can change, applied to the running window.</summary>
    public void ApplySettings() => ApplySettings(displayChanged: false);

    /// <param name="displayChanged">Options that change how items look (names, sizes, colors, sorting, font) changed: rebuild the lists.</param>
    public void ApplySettings(bool displayChanged)
    {
        // theme and density rebuild both item lists: only when they changed (this runs on every settings change)
        bool densityChanged = UiState.Density != _vm.Settings.Density;
        if (displayChanged)
        {
            UiState.ApplyOptions(_vm.Settings);
            UiState.ApplyTheme(RootGrid.ActualTheme == ElementTheme.Dark);
        }
        if (densityChanged || displayChanged)
            UiState.ApplyDensity(_vm.Settings.Density);
        if (RootGrid.RequestedTheme != ThemeFor(_vm.Settings.Theme))
        {
            ApplyThemeSetting(); // rebuilds the lists
        }
        else if (densityChanged || displayChanged)
        {
            _vm.Left.Rebuild();
            _vm.Right.Rebuild();
        }
        if (displayChanged || densityChanged)
        {
            // the density changes the font size too, and the font is set on the rows' style
            LeftPane.ApplyOptions();
            RightPane.ApplyOptions();
            LeftPane.RefreshBindings();
            RightPane.RefreshBindings();
        }
        _vm.Transfers.StartWaiting(); // the number of parallel transfers may have changed
        UpdateTitle();
        ApplyAlwaysOnTop(_vm.Settings.AlwaysOnTop);
        BuildToolbar();
        BuildQuickBars();
        UpdateMenuShortcuts();
        _fkeyModifiers = (KeyModifiers)(-1);
        UpdateFunctionKeyBar(KeyModifiers.None);
        ApplyBars();
    }

    private void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveSettings();
        Notify($"Configuration saved to {SettingsStore.FilePath}", isError: false);
    }

    private async void ExportConfiguration_Click(object sender, RoutedEventArgs e) => await ExportConfigurationAsync(AppWindow.Id);

    private async void ImportConfiguration_Click(object sender, RoutedEventArgs e) => await ImportConfigurationAsync(AppWindow.Id, Dialogs);

    public async Task ExportConfigurationAsync(Microsoft.UI.WindowId owner)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(owner)
            {
                SuggestedFileName = "VibeSalamander-settings",
            };
            picker.FileTypeChoices.Add("Vibe Salamander configuration", new List<string> { ".json" });
            var picked = await picker.PickSaveFileAsync();
            if (string.IsNullOrEmpty(picked?.Path))
                return;
            bool suppressed = _vm.SuppressSettingsSave;
            _vm.SuppressSettingsSave = false;
            _vm.SaveSettings(); // panels' current state into the settings
            _vm.SuppressSettingsSave = suppressed;
            SettingsStore.Export(_vm.Settings, picked.Path);
            Notify($"Configuration exported to {picked.Path}", isError: false);
        }
        catch (Exception ex)
        {
            Notify($"Export failed: {ex.Message}");
        }
    }

    public async Task<bool> ImportConfigurationAsync(Microsoft.UI.WindowId owner, Dialogs dialogs)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner);
            picker.FileTypeFilter.Add(".json");
            var picked = await picker.PickSingleFileAsync();
            path = picked?.Path;
        }
        catch (Exception ex)
        {
            Notify($"Import failed: {ex.Message}");
            return false;
        }
        if (string.IsNullOrEmpty(path))
            return false;

        AppSettings imported;
        try
        {
            imported = SettingsStore.ReadFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await dialogs.MessageAsync("Import Configuration", ex.Message);
            return false;
        }
        ContentDialog confirm = dialogs.Create("Import Configuration",
            new TextBlock { Text = "All current settings are replaced and Vibe Salamander restarts. Running transfers are cancelled.", TextWrapping = TextWrapping.Wrap });
        confirm.PrimaryButtonText = "Import and Restart";
        confirm.CloseButtonText = "Cancel";
        confirm.DefaultButton = ContentDialogButton.Primary;
        if (await dialogs.ShowDialogAsync(confirm) != ContentDialogResult.Primary)
            return false;
        SettingsStore.Save(imported);
        Restart();
        return true;
    }

    public async Task ResetConfigurationAsync(Dialogs dialogs)
    {
        ContentDialog confirm = dialogs.Create("Reset All Settings",
            new TextBlock { Text = "Hot paths, user menu, shortcuts, toolbar and all other settings go back to the defaults, and Vibe Salamander restarts.", TextWrapping = TextWrapping.Wrap });
        confirm.PrimaryButtonText = "Reset and Restart";
        confirm.CloseButtonText = "Cancel";
        confirm.DefaultButton = ContentDialogButton.Close;
        if (await dialogs.ShowDialogAsync(confirm) != ContentDialogResult.Primary)
            return;
        try
        {
            File.Delete(SettingsStore.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"The settings file could not be deleted: {ex.Message}");
            return;
        }
        Restart();
    }

    /// <summary>Saves the settings and restarts (Configuration → General → Language).</summary>
    public void RestartApp()
    {
        _vm.SaveSettings();
        Restart();
    }

    /// <summary>Starts a new instance and closes this one without saving its settings over the new ones.</summary>
    private void Restart()
    {
        _vm.SuppressSettingsSave = true;
        string? exe = Environment.ProcessPath;
        if (exe != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
        _vm.Transfers.CancelAll();
        _closeConfirmed = true;
        _settingsWindow?.Close();
        Close();
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

    // F-key bar: F1..F12 relabelled for the held modifiers (Ctrl, Alt, Shift and combinations)

    private readonly Button[] _fkeyButtons = new Button[12];
    private readonly TextBlock[] _fkeyLabels = new TextBlock[12];
    private readonly Border[] _fkeyCaps = new Border[12];
    private readonly Action?[] _fkeyActions = new Action?[12];
    private KeyModifiers _fkeyModifiers = (KeyModifiers)(-1);

    /// <summary>Short Salamander-style labels for the bottom bar.</summary>
    private static string ShortLabel(CommandId command) => command switch
    {
        CommandId.Help => "Help",
        CommandId.ChangeDriveLeft => "Left",
        CommandId.ChangeDriveRight => "Right",
        CommandId.QuickRename => "Rename",
        CommandId.View => "View",
        CommandId.Edit => "Edit",
        CommandId.EditNewFile => "NewFile",
        CommandId.Copy => "Copy",
        CommandId.Pack => "Pack",
        CommandId.CalculateChecksums => "Checksum",
        CommandId.VerifyChecksums => "Verify",
        CommandId.Unpack => "Unpack",
        CommandId.Move => "Move",
        CommandId.CreateDirectory => "MakeDir",
        CommandId.ChangeDirectory => "ChngDir",
        CommandId.Delete => "Delete",
        CommandId.DeletePermanently => "DelPerm",
        CommandId.Refresh => "Refresh",
        CommandId.MaximizePanel => "Maximize",
        CommandId.SortByName => "Name",
        CommandId.SortByExtension => "Ext",
        CommandId.SortByDate => "Date",
        CommandId.SortBySize => "Size",
        CommandId.SortByAttributes => "Attrs",
        CommandId.Filter => "Filter",
        CommandId.OpenWith => "OpenWith",
        CommandId.ChangeAttributes => "Attrib",
        CommandId.ChangeCase => "Case",
        CommandId.FindFiles => "Find",
        CommandId.ContextMenu => "Context",
        CommandId.OpenActiveFolder => "Explorer",
        CommandId.Properties => "Props",
        CommandId.Exit => "Exit",
        CommandId.DriveInformation => "DrvInfo",
        CommandId.CalculateOccupiedSpace => "DirSize",
        CommandId.CalculateDirectorySizes => "AllSizes",
        CommandId.CompareDirectories => "Compare",
        CommandId.UserMenu => "UsrMenu",
        CommandId.SaveSelection => "SaveSel",
        CommandId.LoadSelection => "LoadSel",
        CommandId.OpenedFilesList => "Files",
        CommandId.WorkingDirectoriesList => "Dirs",
        CommandId.ConnectNetworkDrive => "NetCon",
        CommandId.ConnectFtp => "FTP",
        CommandId.DiskMap => "DiskMap",
        CommandId.TestArchive => "Test",
        CommandId.MakeSelfExtractingArchive => "SFX",
        CommandId.ArchiveComment => "Comment",
        CommandId.DisconnectNetworkDrive => "NetDis",
        CommandId.SharedDirectories => "Shares",
        _ => command.ToString(),
    };

    private static string ModifierText(KeyModifiers mods)
    {
        var parts = new List<string>();
        if ((mods & KeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((mods & KeyModifiers.Shift) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }

    private void BuildFunctionKeyBar()
    {
        for (int i = 0; i < 12; i++)
        {
            FKeyBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var caption = new TextBlock { Text = "F" + (i + 1), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var cap = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x80, 0x80, 0x80)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 1),
                MinWidth = 24,
                Child = caption,
            };
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(cap);
            panel.Children.Add(label);
            var button = new Button
            {
                Content = panel,
                Style = Application.Current.Resources.TryGetValue("VibeFKeyButtonStyle", out object? style) ? style as Style : null,
            };
            int index = i;
            button.Click += (_, _) => _fkeyActions[index]?.Invoke();
            Grid.SetColumn(button, i);
            FKeyBar.Children.Add(button);
            _fkeyButtons[i] = button;
            _fkeyLabels[i] = label;
            _fkeyCaps[i] = cap;
        }
        UpdateFunctionKeyBar(KeyModifiers.None);
    }

    /// <summary>Relabels F1..F12 for the given modifiers from the key map (keys without a command are blank).</summary>
    private void UpdateFunctionKeyBar(KeyModifiers mods)
    {
        if (mods == _fkeyModifiers || _fkeyButtons[0] == null)
            return;
        _fkeyModifiers = mods;
        string prefix = ModifierText(mods);
        bool held = mods != KeyModifiers.None;
        var accent = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? a) ? a as Brush : null;
        var neutral = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x80, 0x80, 0x80));

        for (int i = 0; i < 12; i++)
        {
            int key = VK.F1 + i;
            CommandId command = _vm.KeyMap.Resolve(key, mods);
            string label;
            Action? action = null;
            if (command != CommandId.None)
            {
                label = ShortLabel(command);
                CommandId c = command;
                action = async () => { await _vm.ExecuteAsync(c); UpdateMenuChecks(); PaneView(_vm.Active).FocusList(); };
            }
            else if (mods == KeyModifiers.None && key == VK.F10)
            {
                label = "Menu";
                action = FocusMenu;
            }
            else if (mods == KeyModifiers.Alt && key == VK.F4)
            {
                label = "Exit";
                action = Exit;
            }
            else
            {
                label = string.Empty;
            }

            _fkeyLabels[i].Text = Loc.T(label);
            _fkeyActions[i] = action;
            _fkeyButtons[i].IsEnabled = action != null;
            // with a modifier held, the active keys get accent key caps
            bool highlight = held && action != null && accent != null;
            var capText = (TextBlock)_fkeyCaps[i].Child;
            _fkeyCaps[i].Background = highlight ? accent : neutral;
            if (highlight && Application.Current.Resources.TryGetValue("TextOnAccentFillColorPrimaryBrush", out object? fg) && fg is Brush onAccent)
                capText.Foreground = onAccent;
            else
                capText.ClearValue(TextBlock.ForegroundProperty);
            string chord = held ? $"{prefix}+F{i + 1}" : $"F{i + 1}";
            ToolTipService.SetToolTip(_fkeyButtons[i], action != null ? $"{chord}  {label}" : null);
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

    private static FontIcon CheckIcon(bool on) => new() { Glyph = on ? "\uE73A" : "\uE739" };

    private void UpdateMenuChecks()
    {
        // check boxes as icons: a check mark column would push every icon of the Options menu to the right
        HiddenFilesMenuItem.Icon = CheckIcon(_vm.Settings.ShowHidden);
        AlwaysOnTopMenuItem.Icon = CheckIcon(_vm.Settings.AlwaysOnTop);
        ThemeSystemItem.IsChecked = _vm.Settings.Theme == AppTheme.System;
        ThemeLightItem.IsChecked = _vm.Settings.Theme == AppTheme.Light;
        ThemeDarkItem.IsChecked = _vm.Settings.Theme == AppTheme.Dark;
        ShowToolbarItem.IsChecked = _vm.Settings.ShowTopToolbar;
        ShowHotPathBarItem.IsChecked = _vm.Settings.ShowHotPathBar;
        ShowUserMenuBarItem.IsChecked = _vm.Settings.ShowUserMenuBar;
        ShowDriveBarsItem.IsChecked = _vm.Settings.ShowDriveBars;
        ShowInfoLineItem.IsChecked = _vm.Settings.ShowInformationLine;
        ShowCommandLineItem.IsChecked = _vm.Settings.ShowCommandLine;
        ShowFKeyBarItem.IsChecked = _vm.Settings.ShowFunctionKeyBar;
        ShowTransfersItem.IsChecked = _vm.Settings.ShowTransfers;
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
        RootGrid.RequestedTheme = ThemeFor(_vm.Settings.Theme);
        OnThemeChanged();
    }

    private static ElementTheme ThemeFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void OnThemeChanged()
    {
        UiState.ApplyTheme(RootGrid.ActualTheme == ElementTheme.Dark);
        UpdateTitleBarButtons();
        _settingsWindow?.SetTheme(RootGrid.ActualTheme);
        _vm.Left.Rebuild();
        _vm.Right.Rebuild();
        LeftPane.ApplyOptions();
        RightPane.ApplyOptions();
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
        // the font size follows the density: it is set on the rows' style (ApplyOptions)
        LeftPane.ApplyOptions();
        RightPane.ApplyOptions();
        _vm.Left.Rebuild();
        _vm.Right.Rebuild();
        UpdateMenuChecks();
    }

    public void ApplyAlwaysOnTop(bool onTop)
    {
        // setting the flag (even to its current value) moves the window to the top of the z-order,
        // which would put it over the configuration window on every settings change
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != onTop)
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
        s.ShowTransfers = ShowTransfersItem.IsChecked;
        s.ShowHotPathBar = ShowHotPathBarItem.IsChecked;
        s.ShowUserMenuBar = ShowUserMenuBarItem.IsChecked;
        ApplyBars();
    }

    private void ApplyBars()
    {
        AppSettings s = _vm.Settings;
        ToolbarPanel.Visibility = s.ShowTopToolbar ? Visibility.Visible : Visibility.Collapsed;
        HotPathBar.Visibility = s.ShowHotPathBar && HotPathBar.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UserMenuBar.Visibility = s.ShowUserMenuBar && UserMenuBar.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TopToolbar.Visibility = ToolbarPanel.Visibility == Visibility.Visible || HotPathBar.Visibility == Visibility.Visible ||
                                UserMenuBar.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        CommandLineBar.Visibility = s.ShowCommandLine ? Visibility.Visible : Visibility.Collapsed;
        FKeyBar.Visibility = s.ShowFunctionKeyBar ? Visibility.Visible : Visibility.Collapsed;
        LeftPane.SetBarsVisibility(s.ShowDriveBars, s.ShowInformationLine);
        RightPane.SetBarsVisibility(s.ShowDriveBars, s.ShowInformationLine);
        UpdateTransfersLayout();
        UpdateMenuChecks();
    }

    // ---- transfer panel ------------------------------------------------------------------------

    private const double TransfersSplitterHeight = 6;
    private const double MinTransfersHeight = 80;

    /// <summary>Shows the transfer panel while it has items (unless switched off in Options → Show).</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transfersCloseTimer;

    /// <summary>
    /// Configuration → Main Window: when nothing is running any more, the finished transfers are cleared
    /// after a few seconds, so the panel closes. Transfers that failed or were cancelled stay (their
    /// messages matter), and so does the panel then.
    /// </summary>
    private void ScheduleTransfersAutoClose()
    {
        // an always visible panel keeps its list as a log: nothing is cleared by itself
        int seconds = _vm.Settings.AlwaysShowTransfers ? 0 : _vm.Settings.TransfersAutoCloseSeconds;
        bool idle = !TransfersView.IsHeld && _vm.Transfers.HasItems && _vm.Transfers.ActiveCount == 0 &&
                    _vm.Transfers.Items.Any(i => i.State == TransferState.Completed); // only failures left: the panel stays
        if (seconds <= 0 || !idle)
        {
            _transfersCloseTimer?.Stop();
            TransfersView.StopCloseCountdown();
            return;
        }
        if (_transfersCloseTimer == null)
        {
            _transfersCloseTimer = DispatcherQueue.CreateTimer();
            _transfersCloseTimer.IsRepeating = false;
            _transfersCloseTimer.Tick += (_, _) =>
            {
                TransfersView.StopCloseCountdown();
                if (_vm.Transfers.ActiveCount == 0)
                    _vm.Transfers.ClearSucceeded();
            };
        }
        _transfersCloseTimer.Interval = TimeSpan.FromSeconds(seconds);
        _transfersCloseTimer.Start(); // restarts the countdown when another transfer ends
        TransfersView.StartCloseCountdown(TimeSpan.FromSeconds(seconds));
    }

    private void UpdateTransfersLayout()
    {
        bool show = _vm.Settings.ShowTransfers && (_vm.Settings.AlwaysShowTransfers || _vm.Transfers.HasItems);
        TransfersView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TransfersSplitter.Visibility = show && !TransfersView.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            TransfersRow.Height = new GridLength(0);
            TransfersSplitterRow.Height = new GridLength(0);
            return;
        }
        double margin = TransfersView.Margin.Top + TransfersView.Margin.Bottom;
        if (TransfersView.IsCollapsed)
        {
            TransfersRow.Height = new GridLength(TransfersPanel.HeaderHeight + margin);
            TransfersSplitterRow.Height = new GridLength(0);
        }
        else
        {
            TransfersRow.Height = new GridLength(ClampTransfersHeight(_vm.Settings.TransfersHeight) + margin);
            TransfersSplitterRow.Height = new GridLength(TransfersSplitterHeight);
        }
    }

    private double ClampTransfersHeight(double height)
    {
        double max = RootGrid.ActualHeight > 0 ? Math.Max(MinTransfersHeight, RootGrid.ActualHeight * 0.6) : 600;
        return Math.Clamp(height, MinTransfersHeight, max);
    }

    private bool _resizingTransfers;
    private double _resizeStartY;
    private double _resizeStartHeight;

    private void TransfersSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizingTransfers = TransfersSplitter.CapturePointer(e.Pointer);
        _resizeStartY = e.GetCurrentPoint(RootGrid).Position.Y;
        _resizeStartHeight = ClampTransfersHeight(_vm.Settings.TransfersHeight);
        e.Handled = true;
    }

    private void TransfersSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingTransfers)
            return;
        double y = e.GetCurrentPoint(RootGrid).Position.Y;
        _vm.Settings.TransfersHeight = ClampTransfersHeight(_resizeStartHeight - (y - _resizeStartY));
        UpdateTransfersLayout();
    }

    private void TransfersSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingTransfers = false;
        TransfersSplitter.ReleasePointerCapture(e.Pointer);
    }

    private void TransfersSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _vm.Settings.TransfersHeight = 150;
        UpdateTransfersLayout();
    }

    private bool _closeConfirmed;
    private bool _closePrompting;

    /// <summary>Closing while transfers run asks first, then cancels them.</summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || (_vm.Transfers.ActiveCount == 0 && !_vm.Settings.Confirm.CloseApplication))
            return;
        args.Cancel = true;
        await CloseAfterConfirmAsync();
    }

    /// <summary>
    /// Window.Close() does not raise AppWindow.Closing, so File → Exit and the F-key bar come here too:
    /// asks while transfers are active (once, even when the close button is clicked again), then closes.
    /// </summary>
    private async Task CloseAfterConfirmAsync()
    {
        if (_closePrompting)
            return;
        int count = _vm.Transfers.ActiveCount;
        if (count == 0 && _vm.Settings.Confirm.CloseApplication)
        {
            // Configuration → Confirmations → "Do you want to close Vibe Salamander?"
            _closePrompting = true;
            try
            {
                if (!await ConfirmAsync("Exit Vibe Salamander", "Do you want to close Vibe Salamander?", "Close"))
                    return;
            }
            finally
            {
                _closePrompting = false;
            }
        }
        if (count > 0 && !_vm.Settings.Confirm.ExitWithRunningTransfers)
        {
            _vm.Transfers.CancelAll();
        }
        else if (count > 0)
        {
            bool close;
            _closePrompting = true;
            try
            {
                close = await ConfirmAsync("Exit Vibe Salamander",
                    Loc.F(count == 1 ? "{0} transfer is still running or queued.\nCancel it and exit?" : "{0} transfers are still running or queued.\nCancel them and exit?", count),
                    "Cancel and Exit");
            }
            finally
            {
                _closePrompting = false;
            }
            if (!close)
                return;
            _vm.Transfers.CancelAll();
        }
        _closeConfirmed = true;
        Close();
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

        ContentDialog dialog = Dialogs.Create(title, content);
        dialog.PrimaryButtonText = primaryButton;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
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

        try
        {
            ContentDialogResult result = await Dialogs.ShowDialogAsync(dialog);
            accepted |= result == ContentDialogResult.Primary;
        }
        finally
        {
            PaneView(_vm.Active).FocusList();
        }
        return accepted ? box.Text : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryButton)
    {
        ContentDialog dialog = Dialogs.Create(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = primaryButton;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        try
        {
            return await Dialogs.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
        }
        finally
        {
            PaneView(_vm.Active).FocusList();
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = Dialogs.Create(title, new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono,Consolas") },
            MaxHeight = 480,
        });
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await Dialogs.ShowDialogAsync(dialog);
    }

    public async void ShowAbout()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new Image
        {
            Width = 56,
            Height = 56,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.png"))),
        });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Vibe Salamander", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Tag = "NoLoc" });
        name.Children.Add(new TextBlock { Text = Loc.F("Version {0}", version == null ? "0.1" : $"{version.Major}.{version.Minor}.{version.Build}"), Opacity = 0.7, Tag = "NoLoc" });
        header.Children.Add(name);
        body.Children.Add(header);

        TextBlock Para(string text, double opacity = 1) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = opacity, Tag = "NoLoc" };
        HyperlinkButton Link(string text, string url) =>
            new() { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), Tag = "NoLoc" };

        body.Children.Add(Para(Loc.T("A modern spiritual successor to Altap / Open Salamander: dual-pane, keyboard-first, fast and precise, built with C# and WinUI 3.")));
        body.Children.Add(Para(Loc.T("Written by Claude (Anthropic's AI model), with BrainCluster as its guide.")));
        body.Children.Add(Para("Copyright © 2026 Vibe Salamander Authors", 0.8));
        body.Children.Add(Para(Loc.T("This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It comes with ABSOLUTELY NO WARRANTY."), 0.8));
        body.Children.Add(Link(Loc.T("GNU General Public License v3"), "https://www.gnu.org/licenses/gpl-3.0.html"));

        // Open Salamander: the program this one follows, and the source of the icon
        body.Children.Add(new TextBlock { Text = Loc.T("Credits"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Tag = "NoLoc" });
        body.Children.Add(Para(Loc.T("Vibe Salamander is inspired by Servant / Altap / Open Salamander, created by Petr Šolín and developed by Altap, Fine and the Open Salamander authors. The application icon is based on the Open Salamander icon (© 2023 Open Salamander Authors, GPL-2.0-or-later), recolored.")));
        body.Children.Add(Para("Open Salamander: David Andrš, Lukáš Cerman, Jakub Červený, Tomáš Jelínek, Milan Kaše, Tomáš Kopal, " +
                               "Jan Patera, Martin Přikryl, Juraj Rojko, Jan Ryšavý, Petr Šolín", 0.8));
        body.Children.Add(Link("Open Salamander", "https://github.com/OpenSalamander/salamander"));
        body.Children.Add(Para(Loc.T("Built with .NET, the Windows App SDK (WinUI 3) and SharpCompress (MIT licenses). See LICENSE.txt and CREDITS.md next to the program."), 0.8));
        body.Children.Add(Para(Loc.T("Settings:") + " " + SettingsStore.FilePath, 0.6));
        body.Children.Add(Link("Vibe Salamander", "https://github.com/BrainCluster/vibe-salamander"));

        ContentDialog dialog = Dialogs.Create("About Vibe Salamander", new ScrollViewer { Content = body, MaxHeight = 520 });
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await Dialogs.ShowDialogAsync(dialog);
    }

    /// <summary>Another start of Vibe Salamander (single instance): show this window.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(WindowHandle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public async void Exit() => await CloseAfterConfirmAsync();

    // ---- phase 2 host services -----------------------------------------------------------------

    public Dialogs Dialogs { get; }

    // ---- notifications (instead of message dialogs) -------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notificationTimer;

    public void Notify(string message, bool isError = true)
    {
        Notification.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        Notification.Title = isError ? Loc.T("Couldn't do that") : string.Empty;
        Notification.Message = Loc.T(message);
        Notification.IsOpen = true;
        NotificationHost.Visibility = Visibility.Visible;
        if (_notificationTimer == null)
        {
            _notificationTimer = DispatcherQueue.CreateTimer();
            _notificationTimer.IsRepeating = false;
            _notificationTimer.Tick += (_, _) => HideNotification();
        }
        // long enough to read; longer messages stay a bit longer
        _notificationTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(4 + message.Length / 40.0, 5, 12));
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void HideNotification()
    {
        _notificationTimer?.Stop();
        NotificationHost.Visibility = Visibility.Collapsed;
    }

    private void Notification_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        HideNotification(); // Notify opens it again for the next message
        PaneView(_vm.Active).FocusList();
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
