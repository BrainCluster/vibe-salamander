// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Plugins;
using VibeSalamander.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace VibeSalamander.Views;

/// <summary>
/// A non-modal window a plugin opens (Calculate / Verify Checksums): a list of rows with text columns,
/// a status line, a progress bar and buttons. Plugins call it from any thread; changes are collected and
/// shown together ten times a second, so thousands of rows and progress updates stay cheap.
/// </summary>
public sealed class PluginListWindow : Window, IPluginListWindow
{
    private readonly IReadOnlyList<PluginColumn> _columns;
    private readonly ElementTheme _theme;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _flushTimer;
    private readonly object _sync = new();

    private readonly System.Collections.ObjectModel.ObservableCollection<Row> _shown = new();
    private readonly List<Row> _all = new();          // every row the plugin has (also not yet shown)
    private readonly List<Row> _pendingAdd = new();
    private readonly HashSet<Row> _pendingRemove = new();
    private readonly HashSet<Row> _dirty = new();
    private string? _pendingStatus;
    private (bool Set, double? Value) _pendingProgress;
    private volatile Row? _selected;

    private readonly ListView _list;
    private readonly Grid _header;
    private readonly Grid _table;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly StackPanel _buttonPanel;
    private readonly Dictionary<string, Button> _buttons = new();
    private PluginButton? _default;

    /// <summary>Dialogs shown in this window (Save, Configure, questions).</summary>
    public Dialogs Dialogs { get; }

    public PluginListWindow(string title, IReadOnlyList<PluginColumn> columns, ElementTheme theme)
    {
        _columns = columns.Count > 0 ? columns : new[] { new PluginColumn(string.Empty, 0) };
        _theme = theme;
        _dispatcher = DispatcherQueue;
        Title = title;
        SystemBackdrop = new MicaBackdrop();
        Dialogs = new Dialogs(() => Content.XamlRoot, () => _theme, () => AppWindow.Id);

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 600));

        // header and list share the column widths; together they scroll sideways when the columns are wider
        _header = new Grid { Height = 30 };
        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = _shown,
            ItemTemplate = BuildRowTemplate(),
            IsItemClickEnabled = false,
        };
        if (Application.Current.Resources.TryGetValue("VibeListItemStyle", out object? itemStyle) && itemStyle is Style style)
            _list.ItemContainerStyle = style;
        _list.ContainerContentChanging += List_ContainerContentChanging;
        _list.SelectionChanged += (_, _) => _selected = _list.SelectedItem as Row;
        _list.RightTapped += List_RightTapped;
        _list.KeyDown += List_KeyDown;
        // a double-click on a row presses the default button (Connect, Open, ...)
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem != null && _default is { } button && _buttons.TryGetValue(button.Id, out Button? control) && control.IsEnabled)
                Invoke(button.Click);
        };

        _table = new Grid();
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        _table.Children.Add(_list);
        BuildHeader();

        _scroller = new ScrollViewer
        {
            Content = _table,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _scroller.SizeChanged += (_, _) => UpdateTableWidth();

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "NoLoc" };
        _progress = new ProgressBar { Width = 180, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var bottom = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 10, 12, 12) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        bottom.Children.Add(_progress);
        Grid.SetColumn(_buttonPanel, 2);
        bottom.Children.Add(_buttonPanel);

        var listBorder = new Border
        {
            Child = _scroller,
            Margin = new Thickness(12, 12, 12, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        if (Application.Current.Resources.TryGetValue("VibePaneBorderBrush", out object? border) && border is Brush borderBrush)
            listBorder.BorderBrush = borderBrush;
        if (Application.Current.Resources.TryGetValue("VibePaneBackgroundBrush", out object? background) && background is Brush backgroundBrush)
            listBorder.Background = backgroundBrush;

        var root = new Grid { RequestedTheme = theme };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(listBorder);
        Grid.SetRow(bottom, 1);
        root.Children.Add(bottom);
        root.KeyDown += Root_KeyDown;
        Content = root;

        _flushTimer = _dispatcher.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(100);
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        Closed += (_, _) =>
        {
            IsClosed = true;
            _flushTimer.Stop();
            ClosedHandlers?.Invoke(this, EventArgs.Empty);
            MemoryCleanup.Schedule();
        };
    }

    // ---- layout --------------------------------------------------------------------------------

    private double FixedWidth => _columns.Where(c => c.Width > 0).Sum(c => c.Width);

    /// <summary>The fill column takes the rest of the window, at least 260 px.</summary>
    private void UpdateTableWidth()
    {
        double available = _scroller.ActualWidth;
        if (available <= 0)
            return;
        bool hasFill = _columns.Any(c => c.Width <= 0);
        _table.Width = hasFill ? Math.Max(available, FixedWidth + 260) : Math.Max(available, FixedWidth);
    }

    private static string ColumnXaml(PluginColumn column) =>
        column.Width > 0
            ? $"<ColumnDefinition Width='{column.Width.ToString(CultureInfo.InvariantCulture)}' />"
            : "<ColumnDefinition Width='*' />";

    private DataTemplate BuildRowTemplate()
    {
        var xaml = new StringBuilder("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Height='24'><Grid.ColumnDefinitions>");
        foreach (PluginColumn column in _columns)
            xaml.Append(ColumnXaml(column));
        xaml.Append("</Grid.ColumnDefinitions>");
        for (int i = 0; i < _columns.Count; i++)
        {
            PluginColumn column = _columns[i];
            xaml.Append($"<TextBlock Grid.Column='{i}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='8,0,8,0'");
            if (column.AlignRight)
                xaml.Append(" HorizontalAlignment='Right'");
            if (column.Monospace)
                xaml.Append(" FontFamily='Cascadia Mono,Consolas'");
            xaml.Append(" />");
        }
        xaml.Append("</Grid></DataTemplate>");
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml.ToString());
    }

    private void BuildHeader()
    {
        _header.Padding = new Thickness(6, 0, 4, 0); // like the rows' container padding
        for (int i = 0; i < _columns.Count; i++)
        {
            PluginColumn column = _columns[i];
            _header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column.Width > 0 ? new GridLength(column.Width) : new GridLength(1, GridUnitType.Star),
            });
            var text = new TextBlock
            {
                Text = column.Header,
                Tag = "NoLoc",
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = column.AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.8,
            };
            Grid.SetColumn(text, i);
            _header.Children.Add(text);
        }
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is Row row && args.ItemContainer.ContentTemplateRoot is Grid grid)
            Fill(grid, row);
    }

    private void Fill(Grid grid, Row row)
    {
        string[] cells = row.Snapshot();
        for (int i = 0; i < grid.Children.Count && i < cells.Length; i++)
            if (grid.Children[i] is TextBlock text && text.Text != cells[i])
                text.Text = cells[i];
    }

    // ---- updates from the plugin (any thread) ---------------------------------------------------

    private void Flush()
    {
        List<Row> add, remove, dirty;
        string? status;
        (bool Set, double? Value) progress;
        lock (_sync)
        {
            add = _pendingAdd.ToList();
            _pendingAdd.Clear();
            remove = _pendingRemove.ToList();
            _pendingRemove.Clear();
            dirty = _dirty.ToList();
            _dirty.Clear();
            status = _pendingStatus;
            _pendingStatus = null;
            progress = _pendingProgress;
            _pendingProgress = default;
        }
        foreach (Row row in add)
            _shown.Add(row);
        foreach (Row row in remove)
            _shown.Remove(row);
        foreach (Row row in dirty)
            if (_list.ContainerFromItem(row) is ListViewItem { ContentTemplateRoot: Grid grid })
                Fill(grid, row);
        if (status != null)
            _status.Text = status;
        if (progress.Set)
        {
            _progress.Visibility = progress.Value == null ? Visibility.Collapsed : Visibility.Visible;
            if (progress.Value is double value)
                _progress.Value = Math.Clamp(value, 0, 1);
        }
        if (add.Count > 0 && _list.SelectedIndex < 0 && _shown.Count > 0)
            _list.SelectedIndex = 0;
    }

    public IPluginRow AddRow(IReadOnlyList<string> cells)
    {
        var row = new Row(this, _columns.Count, cells);
        lock (_sync)
        {
            _all.Add(row);
            _pendingAdd.Add(row);
        }
        return row;
    }

    public IReadOnlyList<IPluginRow> Rows
    {
        get
        {
            lock (_sync)
                return _all.ToList();
        }
    }

    public IPluginRow? SelectedRow => _selected;

    public void SetStatus(string text)
    {
        lock (_sync)
            _pendingStatus = text;
    }

    public void SetProgress(double? value)
    {
        lock (_sync)
            _pendingProgress = (true, value);
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    public void SetButtons(IReadOnlyList<PluginButton> buttons) => OnUi(() =>
    {
        _buttonPanel.Children.Clear();
        _buttons.Clear();
        _default = buttons.FirstOrDefault(b => b.IsDefault);
        foreach (PluginButton definition in buttons)
        {
            var button = new Button { Content = definition.Text, MinWidth = 96 };
            if (definition.IsDefault && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent) && accent is Style accentStyle)
                button.Style = accentStyle;
            PluginButton d = definition;
            button.Click += (_, _) => Invoke(d.Click);
            _buttons[definition.Id] = button;
            _buttonPanel.Children.Add(button);
        }
    });

    public void UpdateButton(string id, string? text = null, bool? enabled = null) => OnUi(() =>
    {
        if (!_buttons.TryGetValue(id, out Button? button))
            return;
        if (text != null)
            button.Content = text;
        if (enabled != null)
            button.IsEnabled = enabled.Value;
    });

    public Func<IPluginRow, IReadOnlyList<PluginMenuItem>>? RowMenu { get; set; }

    private EventHandler? ClosedHandlers;

    event EventHandler? IPluginListWindow.Closed
    {
        add => ClosedHandlers += value;
        remove => ClosedHandlers -= value;
    }

    public bool IsClosed { get; private set; }

    void IPluginListWindow.Close() => OnUi(() =>
    {
        if (!IsClosed)
            Close();
    });

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // a plugin's mistake must not take the window down
        }
    }

    // ---- row menu and keys ---------------------------------------------------------------------

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RowMenu == null || (e.OriginalSource as FrameworkElement)?.DataContext is not Row row)
            return;
        _list.SelectedItem = row;
        var menu = new MenuFlyout();
        foreach (PluginMenuItem item in SafeMenu(row))
        {
            var entry = new MenuFlyoutItem { Text = item.Text, IsEnabled = item.IsEnabled };
            if (!string.IsNullOrEmpty(item.ShortcutText))
                entry.KeyboardAcceleratorTextOverride = item.ShortcutText;
            PluginMenuItem i = item;
            entry.Click += (_, _) => Invoke(i.Execute);
            menu.Items.Add(entry);
        }
        if (menu.Items.Count > 0)
            menu.ShowAt(_list, e.GetPosition(_list));
        e.Handled = true;
    }

    private IReadOnlyList<PluginMenuItem> SafeMenu(Row row)
    {
        try
        {
            return RowMenu?.Invoke(row) ?? Array.Empty<PluginMenuItem>();
        }
        catch (Exception)
        {
            return Array.Empty<PluginMenuItem>();
        }
    }

    /// <summary>The row menu's shortcut keys ("Ctrl+C", "Delete") work on the selected row.</summary>
    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RowMenu == null || _list.SelectedItem is not Row row)
            return;
        string keys = KeyText(e.Key);
        if (keys.Length == 0)
            return;
        foreach (PluginMenuItem item in SafeMenu(row))
        {
            if (item.IsEnabled && string.Equals(item.ShortcutText, keys, StringComparison.OrdinalIgnoreCase))
            {
                Invoke(item.Execute);
                e.Handled = true;
                return;
            }
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
            return;
        if (e.Key == VirtualKey.Enter && _default is { } button && _buttons.TryGetValue(button.Id, out Button? control) && control.IsEnabled)
        {
            Invoke(button.Click);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private static string KeyText(VirtualKey key)
    {
        string name = key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (key - VirtualKey.A))).ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (key - VirtualKey.F1 + 1),
            _ => string.Empty,
        };
        if (name.Length == 0)
            return string.Empty;
        var prefix = new StringBuilder();
        if (KeyboardState.IsDown(KeyboardState.Control))
            prefix.Append("Ctrl+");
        if (KeyboardState.IsDown(KeyboardState.Shift))
            prefix.Append("Shift+");
        if (KeyboardState.IsDown(KeyboardState.Alt))
            prefix.Append("Alt+");
        return prefix + name;
    }

    // ---- rows ----------------------------------------------------------------------------------

    private void CellChanged(Row row)
    {
        lock (_sync)
            _dirty.Add(row);
    }

    private void RowRemoved(Row row)
    {
        lock (_sync)
        {
            _all.Remove(row);
            if (!_pendingAdd.Remove(row))
                _pendingRemove.Add(row);
            _dirty.Remove(row);
        }
    }

    private sealed class Row : IPluginRow
    {
        private readonly PluginListWindow _owner;
        private readonly string[] _cells;

        public Row(PluginListWindow owner, int columns, IReadOnlyList<string> cells)
        {
            _owner = owner;
            _cells = new string[columns];
            for (int i = 0; i < columns; i++)
                _cells[i] = i < cells.Count ? cells[i] ?? string.Empty : string.Empty;
        }

        public string this[int column]
        {
            get
            {
                lock (_cells)
                    return column >= 0 && column < _cells.Length ? _cells[column] : string.Empty;
            }
            set
            {
                if (column < 0 || column >= _cells.Length)
                    return;
                lock (_cells)
                {
                    if (_cells[column] == value)
                        return;
                    _cells[column] = value ?? string.Empty;
                }
                _owner.CellChanged(this);
            }
        }

        public string[] Snapshot()
        {
            lock (_cells)
                return (string[])_cells.Clone();
        }

        public object? Tag { get; set; }

        public void Remove() => _owner.RowRemoved(this);
    }
}
