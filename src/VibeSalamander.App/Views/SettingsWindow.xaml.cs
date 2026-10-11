using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>What the settings window needs from the main window.</summary>
public interface ISettingsHost
{
    MainViewModel ViewModel { get; }

    /// <summary>Saves the settings and starts Vibe Salamander again (a new language).</summary>
    void RestartApp();

    /// <summary>Re-applies theme, density, bars, toolbar, shortcuts and menus after a change.</summary>
    void ApplySettings();

    /// <summary>Like ApplySettings; displayChanged = how items look changed (names, sizes, colors, sorting, font).</summary>
    void ApplySettings(bool displayChanged);

    Task ExportConfigurationAsync(Microsoft.UI.WindowId owner);

    /// <summary>Imports a configuration file; returns true when the app restarts to apply it.</summary>
    Task<bool> ImportConfigurationAsync(Microsoft.UI.WindowId owner, Dialogs dialogs);

    /// <summary>Deletes the settings and restarts with the defaults (asks first).</summary>
    Task ResetConfigurationAsync(Dialogs dialogs);
}

/// <summary>
/// Options → Configuration: everything that used to need editing settings.json, in one non-modal
/// window (Files-style navigation). Changes apply immediately and are saved with the other settings.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly MainViewModel _vm;
    private readonly Dialogs _dialogs;
    private bool _loading;

    public SettingsWindow(ISettingsHost host, ElementTheme theme, string? page = null)
    {
        InitializeComponent();
        _host = host;
        _vm = host.ViewModel;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarText);
        RootGrid.RequestedTheme = theme;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 740));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        _dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme, () => AppWindow.Id);
        Loc.Translate(Nav);
        TitleBarText.Text = Loc.T(TitleBarText.Text);
        Title = Loc.T(Title);
        ShowPage(page ?? "General");
    }

    public void SetTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    /// <summary>
    /// Makes the main window the owner: an owned window is always above its owner (also when the owner
    /// is "always on top"), is minimized with it and has no taskbar button of its own.
    /// </summary>
    public void SetOwner(IntPtr owner)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
    }

    private const int GwlpHwndParent = -8;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>Selects a page by its tag (see SettingsWindow.xaml, e.g. "General", "Confirmations", "Keyboard").</summary>
    public void ShowPage(string tag)
    {
        foreach (object item in Nav.MenuItems)
        {
            if (item is NavigationViewItem nvi && nvi.Tag as string == tag)
            {
                Nav.SelectedItem = nvi;
                return;
            }
        }
    }

    private string _currentPage = "General";

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        Render((args.SelectedItem as NavigationViewItem)?.Tag as string ?? "General");

    private void Render(string tag)
    {
        _currentPage = tag;
        _loading = true;
        try
        {
            PageHost.Content = tag switch
            {
                "RecycleBin" => BuildRecycleBinPage(),
                "Confirmations" => BuildConfirmationsPage(),
                "Panels" => BuildPanelOptionsPage(),
                "Views" => BuildViewsPage(),
                "Appearance" => BuildAppearancePage(),
                "Colors" => BuildColorsPage(),
                "MainWindow" => BuildMainWindowPage(),
                "Drives" => BuildDrivesPage(),
                "ChangeDrive" => BuildChangeDrivePage(),
                "Viewers" => BuildProgramsPage(viewers: true),
                "Editors" => BuildProgramsPage(viewers: false),
                "History" => BuildHistoryPage(),
                "Keyboard" => BuildKeyboardPage(),
                "Toolbar" => BuildToolbarPage(),
                "HotPaths" => BuildHotPathsPage(),
                "UserMenu" => BuildUserMenuPage(),
                "Backup" => BuildBackupPage(),
                "Plugins" => BuildPluginsPage(),
                _ => BuildGeneralPage(),
            };
        }
        finally
        {
            _loading = false;
        }
        Loc.Translate(PageHost.Content);
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <param name="display">The change affects how the panels look (rebuilds the lists).</param>
    private void Changed(bool display = false)
    {
        if (_loading)
            return;
        _host.ApplySettings(display);
        _vm.SaveSettings();
    }

    private DispatcherQueueTimer? _debounce;

    /// <summary>Changed(display: true) once the user stops dragging (color pickers fire on every move).</summary>
    private void ChangedDebounced()
    {
        if (_loading)
            return;
        if (_debounce == null)
        {
            _debounce = DispatcherQueue.CreateTimer();
            _debounce.Interval = TimeSpan.FromMilliseconds(250);
            _debounce.IsRepeating = false;
            _debounce.Tick += (_, _) => Changed(display: true);
        }
        _debounce.Stop();
        _debounce.Start();
    }

    // ---- building blocks ---------------------------------------------------------------------

    private static StackPanel Page(string title, string? subtitle = null)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, Style = Application.Current.Resources["TitleTextBlockStyle"] as Style, Margin = new Thickness(0, 0, 0, 4) });
        if (subtitle != null)
            panel.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) });
        return panel;
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
        Margin = new Thickness(0, 18, 0, 2),
    };

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

    /// <summary>A settings card: label (+ description) on the left, a control on the right.</summary>
    private Style? ThemedStyle(string key) => RootGrid.Resources.TryGetValue(key, out object? style) ? style as Style : null;

    private Border Card(string label, string? description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            texts.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        grid.Children.Add(texts);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Child = grid, Style = ThemedStyle("SettingsCardStyle") };
    }

    private Border Toggle(string label, string? description, bool value, Action<bool> set, bool display = false)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0 };
        toggle.Toggled += (_, _) =>
        {
            set(toggle.IsOn);
            Changed(display);
        };
        return Card(label, description, toggle);
    }

    /// <summary>A whole number setting.</summary>
    private Border Number(string label, string? description, double value, double min, double max, Action<int> set, bool display = false)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
            MinWidth = 140,
        };
        box.ValueChanged += (_, args) =>
        {
            if (double.IsNaN(args.NewValue))
                return;
            set((int)Math.Round(Math.Clamp(args.NewValue, min, max)));
            Changed(display);
        };
        return Card(label, description, box);
    }

    /// <summary>A text setting (saved as you type).</summary>
    private Border TextSetting(string label, string? description, string value, Action<string> set, double width = 320,
        string? placeholder = null, bool display = false)
    {
        var box = new TextBox { Text = value, Width = width, PlaceholderText = placeholder ?? string.Empty, IsSpellCheckEnabled = false };
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            Changed(display);
        };
        return Card(label, description, box);
    }

    /// <summary>A color swatch that opens a color picker; "" = the scheme's color (Reset).</summary>
    private FrameworkElement ColorButton(string value, Windows.UI.Color fallback, Action<string> set)
    {
        var swatch = new Border { Width = 36, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
        swatch.BorderBrush = ThemedStyle("SettingsFrameStyle") is { } ? (Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush) : null;
        void Show(string hex) => swatch.Background = new SolidColorBrush(UiState.ParseColor(hex) ?? fallback);
        Show(value);
        var picker = new ColorPicker
        {
            Color = UiState.ParseColor(value) ?? fallback,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = true,
        };
        var reset = new Button { Content = "Use the scheme color", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        var flyoutContent = new StackPanel();
        flyoutContent.Children.Add(picker);
        flyoutContent.Children.Add(reset);
        var button = new Button { Content = swatch, Padding = new Thickness(6), Flyout = new Flyout { Content = flyoutContent } };
        picker.ColorChanged += (_, args) =>
        {
            string hex = UiState.ToHex(args.NewColor);
            Show(hex);
            set(hex);
            ChangedDebounced();
        };
        reset.Click += (_, _) =>
        {
            set(string.Empty);
            Show(string.Empty);
            button.Flyout?.Hide();
            Changed(display: true);
        };
        return button;
    }

    private Border Choice<T>(string label, string? description, (string Text, T Value)[] options, T value, Action<T> set, bool display = false)
    {
        var combo = new ComboBox { MinWidth = 180 };
        foreach (var (text, _) in options)
            combo.Items.Add(Loc.T(text));
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, value)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                set(options[combo.SelectedIndex].Value);
                Changed(display);
            }
        };
        return Card(label, description, combo);
    }

    private static Button IconButton(string glyph, string tip, string? text = null)
    {
        object content = text == null
            ? new FontIcon { Glyph = glyph, FontSize = 13 }
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = text } },
            };
        var button = new Button { Content = content };
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    // ---- Views (per panel: view mode and Detailed columns) ------------------------------------------------------------------------------

    private UIElement BuildViewsPage()
    {
        StackPanel page = Page("Views", "View and Detailed view columns of each panel. Column widths are changed by dragging the column header grips.");
        var views = new[]
        {
            ("Brief", ViewMode.Brief), ("Detailed", ViewMode.Detailed), ("Icons", ViewMode.Icons),
            ("Thumbnails", ViewMode.Thumbnails), ("Tiles", ViewMode.Tiles),
        };
        foreach (var (title, pane) in new[] { ("Left panel", _vm.Left), ("Right panel", _vm.Right) })
        {
            PaneViewModel p = pane;
            page.Children.Add(Section(title));
            page.Children.Add(Choice("View", "Alt+1…5", views, p.ViewMode, v => p.ViewMode = v));
            foreach (DetailColumn column in Enum.GetValues<DetailColumn>())
            {
                DetailColumn c = column;
                string label = column switch
                {
                    DetailColumn.Ext => "Extension column",
                    DetailColumn.Attr => "Attributes column",
                    _ => column + " column",
                };
                page.Children.Add(Toggle(label, null, p.Columns.IsVisible(c), v => p.Columns.SetVisible(c, v)));
            }
            var reset = IconButton("", "Reset", "Reset columns");
            reset.Click += (_, _) =>
            {
                p.Columns.Reset();
                _vm.SaveSettings();
                Render(_currentPage); // the toggles show the new state
            };
            page.Children.Add(Card("Columns", "All columns with their default widths.", reset));
        }
        return page;
    }

    // ---- Keyboard ----------------------------------------------------------------------------

    private KeyMap? _keys;
    private CommandId _selectedCommand = CommandId.None;

    private UIElement BuildKeyboardPage()
    {
        _keys = _vm.KeyMap.Clone();
        StackPanel page = Page("Keyboard",
            "Shortcuts of every command. Select a command, then add a shortcut by pressing it, or remove one. " +
            "A shortcut used by another command moves to the selected one.");

        var search = new TextBox { PlaceholderText = "Search commands or shortcuts", Margin = new Thickness(0, 0, 0, 6) };
        var list = new ListView { Height = 330, SelectionMode = ListViewSelectionMode.Single };
        var detail = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        var reset = IconButton("", "Back to the Salamander shortcuts", "Reset all shortcuts");

        void Fill()
        {
            string q = search.Text.Trim();
            list.Items.Clear();
            string? lastCategory = null;
            foreach (CommandId command in Enum.GetValues<CommandId>().Where(c => c is not (CommandId.None or CommandId.GoToHotPath0 or CommandId.SetHotPath0)))
            {
                string name = CommandCatalog.DisplayName(command);
                string shortcuts = string.Join(",  ", _keys!.GetShortcuts(command).Select(KeyMap.Describe));
                if (q.Length > 0 && !name.Contains(q, StringComparison.CurrentCultureIgnoreCase) &&
                    !shortcuts.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                string category = CommandCatalog.Category(command);
                if (category != lastCategory && q.Length == 0)
                {
                    list.Items.Add(new ListViewItem
                    {
                        Content = new TextBlock { Text = category, Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style },
                        IsEnabled = false,
                        Margin = new Thickness(0, 6, 0, 0),
                    });
                    lastCategory = category;
                }
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
                var keys = new TextBlock { Text = shortcuts, Opacity = 0.75, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12 };
                Grid.SetColumn(keys, 1);
                row.Children.Add(keys);
                var item = new ListViewItem { Content = row, Tag = command, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                list.Items.Add(item);
                if (command == _selectedCommand)
                    list.SelectedItem = item;
            }
        }

        void Apply()
        {
            _vm.SetKeyMap(_keys!.Clone());
            _host.ApplySettings();
            _vm.SaveSettings();
        }

        void ShowDetail()
        {
            detail.Children.Clear();
            if (_selectedCommand == CommandId.None)
            {
                detail.Children.Add(Hint("Select a command above."));
                return;
            }
            detail.Children.Add(new TextBlock { Text = CommandCatalog.DisplayName(_selectedCommand), Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style });
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (KeyChord chord in _keys!.GetShortcuts(_selectedCommand))
            {
                KeyChord c = chord;
                var chip = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { new TextBlock { Text = KeyMap.Describe(c) }, new FontIcon { Glyph = "", FontSize = 10 } },
                    },
                };
                ToolTipService.SetToolTip(chip, "Remove this shortcut");
                chip.Click += (_, _) =>
                {
                    _keys.Unbind(c);
                    Apply();
                    Fill();
                    ShowDetail();
                };
                chips.Children.Add(chip);
            }
            if (chips.Children.Count == 0)
                chips.Children.Add(Hint("No shortcut."));
            detail.Children.Add(chips);

            // capture: press the new shortcut in this box
            var capture = new TextBox
            {
                PlaceholderText = "Click here and press the new shortcut",
                IsReadOnly = true,
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var conflict = Hint(string.Empty);
            var assign = new Button { Content = "Add shortcut", IsEnabled = false };
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent))
                assign.Style = accent as Style;
            KeyChord? captured = null;
            capture.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                int key = (int)e.Key;
                if (key is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
                    return; // a modifier on its own
                var chord = new KeyChord(key, Services.KeyboardState.CurrentModifiers());
                captured = chord;
                capture.Text = KeyMap.Describe(chord);
                CommandId current = _keys.Resolve(chord.Key, chord.Modifiers);
                conflict.Text = current == CommandId.None || current == _selectedCommand
                    ? string.Empty
                    : $"Used by \"{CommandSafeName(current)}\" — adding it here removes it there.";
                assign.IsEnabled = current != _selectedCommand;
            };
            assign.Click += (_, _) =>
            {
                if (captured is not KeyChord chord)
                    return;
                _keys.Bind(chord, _selectedCommand);
                Apply();
                Fill();
                ShowDetail();
            };
            var captureRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            captureRow.Children.Add(capture);
            captureRow.Children.Add(assign);
            detail.Children.Add(captureRow);
            detail.Children.Add(conflict);
        }

        static string CommandSafeName(CommandId c) => CommandCatalog.DisplayName(c);

        search.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListViewItem { Tag: CommandId command })
            {
                _selectedCommand = command;
                ShowDetail();
            }
        };
        reset.Click += (_, _) =>
        {
            _keys = KeyMap.CreateSalamanderDefaults();
            Apply();
            Fill();
            ShowDetail();
        };

        Fill();
        ShowDetail();
        page.Children.Add(search);
        page.Children.Add(new Border { Child = list, Style = ThemedStyle("SettingsFrameStyle") });
        page.Children.Add(detail);
        page.Children.Add(Section("Defaults"));
        page.Children.Add(Card("Salamander shortcuts", "Removes all your changes.", reset));
        return page;
    }

    // ---- Toolbar -----------------------------------------------------------------------------

    private static string ToolbarText(string id) => id switch
    {
        CommandCatalog.SeparatorId => "──── Separator ────",
        CommandCatalog.ViewMenuId => "View (drop-down)",
        _ => Enum.TryParse(id, out CommandId c) ? CommandCatalog.DisplayName(c) : id,
    };

    private static string ToolbarGlyph(string id) => id switch
    {
        CommandCatalog.SeparatorId => "",
        CommandCatalog.ViewMenuId => "",
        _ => Enum.TryParse(id, out CommandId c) ? CommandCatalog.Glyph(c) : "",
    };

    private static ListViewItem ToolbarRow(string id) => new()
    {
        Tag = id,
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new FontIcon { Glyph = ToolbarGlyph(id), FontSize = 14, FontFamily = Application.Current.Resources["VibeIconFont"] as FontFamily ?? new FontFamily("Segoe Fluent Icons") },
                new TextBlock { Text = ToolbarText(id) },
            },
        },
    };

    private UIElement BuildToolbarPage()
    {
        AppSettings s = _vm.Settings;
        StackPanel page = Page("Toolbar", "Choose the buttons of the top toolbar and their order.");

        var available = new ListView { Height = 420, Width = 330, SelectionMode = ListViewSelectionMode.Single };
        var current = new ListView { Height = 420, Width = 330, SelectionMode = ListViewSelectionMode.Single };
        var filter = new TextBox { PlaceholderText = "Search commands", Width = 330 };

        void FillAvailable()
        {
            available.Items.Clear();
            string q = filter.Text.Trim();
            available.Items.Add(ToolbarRow(CommandCatalog.SeparatorId));
            if (!s.Toolbar.Contains(CommandCatalog.ViewMenuId))
                available.Items.Add(ToolbarRow(CommandCatalog.ViewMenuId));
            foreach (CommandId c in CommandCatalog.ToolbarCommands)
            {
                string id = c.ToString();
                if (s.Toolbar.Contains(id))
                    continue;
                if (q.Length > 0 && !CommandCatalog.DisplayName(c).Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    continue;
                available.Items.Add(ToolbarRow(id));
            }
        }
        void FillCurrent(int select = -1)
        {
            current.Items.Clear();
            foreach (string id in s.Toolbar)
                current.Items.Add(ToolbarRow(id));
            if (select >= 0 && select < current.Items.Count)
                current.SelectedIndex = select;
        }
        void Apply(int select)
        {
            FillCurrent(select);
            FillAvailable();
            Changed();
        }

        var add = IconButton("", "Add to the toolbar", "Add");
        var remove = IconButton("", "Remove from the toolbar", "Remove");
        var up = IconButton("", "Move up");
        var down = IconButton("", "Move down");
        var reset = IconButton("", "Default toolbar", "Reset");
        add.Click += (_, _) =>
        {
            if (available.SelectedItem is not ListViewItem { Tag: string id })
                return;
            int at = current.SelectedIndex >= 0 ? current.SelectedIndex + 1 : s.Toolbar.Count;
            s.Toolbar.Insert(at, id);
            Apply(at);
        };
        available.DoubleTapped += (sender, e) =>
        {
            if (available.SelectedItem is ListViewItem { Tag: string id })
            {
                int at = current.SelectedIndex >= 0 ? current.SelectedIndex + 1 : s.Toolbar.Count;
                s.Toolbar.Insert(at, id);
                Apply(at);
            }
        };
        remove.Click += (_, _) =>
        {
            int i = current.SelectedIndex;
            if (i < 0)
                return;
            s.Toolbar.RemoveAt(i);
            Apply(Math.Min(i, s.Toolbar.Count - 1));
        };
        void Move(int delta)
        {
            int i = current.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= s.Toolbar.Count)
                return;
            (s.Toolbar[i], s.Toolbar[j]) = (s.Toolbar[j], s.Toolbar[i]);
            Apply(j);
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        reset.Click += (_, _) =>
        {
            s.Toolbar = CommandCatalog.DefaultToolbar();
            Apply(-1);
        };
        filter.TextChanged += (_, _) => FillAvailable();

        FillAvailable();
        FillCurrent();

        Border Framed(UIElement child) => new() { Child = child, Style = ThemedStyle("SettingsFrameStyle") };
        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(new TextBlock { Text = "Available", Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style });
        left.Children.Add(filter);
        left.Children.Add(Framed(available));
        var middle = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (Button b in new[] { add, remove, up, down, reset })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            middle.Children.Add(b);
        }
        var right = new StackPanel { Spacing = 6 };
        right.Children.Add(new TextBlock { Text = "On the toolbar", Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style });
        right.Children.Add(new Border { Height = 32 });
        right.Children.Add(Framed(current));
        var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        layout.Children.Add(left);
        layout.Children.Add(middle);
        layout.Children.Add(right);
        page.Children.Add(layout);
        return page;
    }

    // ---- Hot paths ---------------------------------------------------------------------------

    private UIElement BuildHotPathsPage()
    {
        AppSettings s = _vm.Settings;
        StackPanel page = Page("Hot Paths",
            "Ctrl+1…9 goes to a hot path; Ctrl+Shift+1…9 makes the active panel's directory that hot path. The optional name is shown in menus and on the hot path bar. " +
            "They are also listed in the drive menu (Alt+F1 / Alt+F2) and on the hot path bar.");
        for (int i = 1; i < 10; i++) // no hot path 0: Windows takes Ctrl+Shift+0 for the keyboard layout
        {
            int n = i;
            var box = new TextBox { Text = s.HotPaths[n], PlaceholderText = "not set", Width = 360 };
            box.TextChanged += (_, _) =>
            {
                // also raised (asynchronously, after _loading) for the initial text
                string path = box.Text.Trim();
                if (path == s.HotPaths[n])
                    return;
                s.HotPaths[n] = path;
                Changed();
            };
            var browse = IconButton("", "Browse");
            browse.Click += async (_, _) =>
            {
                string? folder = await _dialogs.PickFolderAsync();
                if (folder != null)
                    box.Text = folder;
            };
            var current = IconButton("", "The active panel's directory");
            current.Click += (_, _) =>
            {
                PaneViewModel active = _vm.Active;
                box.Text = active.IsThisPc ? ThisPcProvider.RootPath : active.Path;
            };
            var clear = IconButton("", "Clear");
            clear.Click += (_, _) => box.Text = string.Empty;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(HotPathNameBox(n));
            row.Children.Add(box);
            row.Children.Add(browse);
            row.Children.Add(current);
            row.Children.Add(clear);
            page.Children.Add(Card($"Ctrl+{n}", null, row));
        }
        return page;
    }

    // ---- User menu ---------------------------------------------------------------------------

    private UIElement BuildUserMenuPage()
    {
        AppSettings s = _vm.Settings;
        StackPanel page = Page("User Menu", "Commands of the user menu (F9) and the user menu bar. Variables such as $(FullName) are replaced when a command runs.");
        var preview = new StackPanel { Spacing = 2 };
        void FillPreview()
        {
            preview.Children.Clear();
            foreach (UserMenuItem item in s.UserMenu)
            {
                if (item.IsSeparator)
                {
                    preview.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Style = ThemedStyle("SettingsDividerStyle") });
                    continue;
                }
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new FontIcon { Glyph = "", FontSize = 13 });
                row.Children.Add(new TextBlock { Text = item.Name, MinWidth = 260 });
                row.Children.Add(new TextBlock { Text = $"{item.Command} {item.Arguments}".Trim(), Opacity = 0.65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
                preview.Children.Add(row);
            }
            if (s.UserMenu.Count == 0)
                preview.Children.Add(Hint("The user menu is empty."));
        }
        FillPreview();
        var edit = IconButton("", "Edit the user menu", "Edit...");
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent))
            edit.Style = accent as Style;
        edit.Click += async (_, _) =>
        {
            List<UserMenuItem>? edited = await _dialogs.EditUserMenuAsync(s.UserMenu);
            if (edited == null)
                return;
            s.UserMenu = edited;
            FillPreview();
            Changed();
        };
        page.Children.Add(Card("User menu", $"{s.UserMenu.Count(i => !i.IsSeparator)} commands", edit));
        page.Children.Add(Section("Commands"));
        page.Children.Add(preview);
        page.Children.Add(Section("Variables"));
        foreach (var (variable, meaning) in UserMenu.Variables)
            page.Children.Add(Hint($"{variable}   {meaning}"));
        return page;
    }

    // ---- Backup --------------------------------------------------------------------------------

    private UIElement BuildBackupPage()
    {
        StackPanel page = Page("Backup", "All settings live in one file: " + SettingsStore.FilePath);
        var export = IconButton("", "Save the configuration to a file", "Export...");
        export.Click += async (_, _) => await _host.ExportConfigurationAsync(AppWindow.Id);
        var import = IconButton("", "Load a configuration file (restarts)", "Import...");
        import.Click += async (_, _) =>
        {
            // on success the main window restarts the app and closes this window itself
            await _host.ImportConfigurationAsync(AppWindow.Id, _dialogs);
        };
        var save = IconButton("", "Write the settings file now", "Save now");
        save.Click += (_, _) => _vm.SaveSettings();
        var folder = IconButton("", "Open the settings folder in Explorer", "Open folder");
        folder.Click += (_, _) => Core.Interop.ShellOperations.OpenInExplorer(SettingsStore.Directory);
        var reset = IconButton("", "Delete all settings (restarts)", "Reset...");
        reset.Click += async (_, _) => await _host.ResetConfigurationAsync(_dialogs);

        page.Children.Add(Card("Export configuration", "A copy of all settings, to move them to another computer or keep as a backup.", export));
        page.Children.Add(Card("Import configuration", "Replaces all settings with a file exported before. Vibe Salamander restarts.", import));
        page.Children.Add(Card("Save configuration", "Settings are also saved when Vibe Salamander closes.", save));
        page.Children.Add(Card("Settings folder", null, folder));
        page.Children.Add(Section("Start over"));
        page.Children.Add(Card("Reset all settings", "Everything back to the defaults (hot paths, user menu, shortcuts...). Vibe Salamander restarts.", reset));
        return page;
    }
}
