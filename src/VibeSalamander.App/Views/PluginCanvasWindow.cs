// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using VibeSalamander.Plugins;
using VibeSalamander.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace VibeSalamander.Views;

/// <summary>
/// A window with a picture a plugin draws (Disk Map): a heading and a choice of views above it, labels and
/// outlines over it, a tip that follows the pointer, a legend below it, and a status line, a progress bar
/// and buttons at the bottom. Plugins call it from any thread.
/// </summary>
public sealed class PluginCanvasWindow : Window, IPluginCanvasWindow
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ElementTheme _theme;
    private readonly TextBlock _heading;
    private readonly SelectorBar _views;
    private readonly Grid _surface;
    private readonly Image _image;
    private readonly Canvas _labels;
    private readonly Polygon _hover;
    private readonly Polygon _selected;
    private readonly Border _tip;
    private readonly TextBlock _tipText;
    private readonly StackPanel _legend;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly StackPanel _buttonPanel;
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly UserControl _focusHost;
    private WriteableBitmap? _bitmap;
    private bool _settingViews;

    private volatile int _pixelWidth = 1;
    private volatile int _pixelHeight = 1;
    private double _scale = 1;

    public PluginCanvasWindow(string title, ElementTheme theme)
    {
        _dispatcher = DispatcherQueue;
        _theme = theme;
        Title = title;
        SystemBackdrop = new MicaBackdrop();
        string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 820));

        // heading and views
        _heading = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "NoLoc",
        };
        _views = new SelectorBar { VerticalAlignment = VerticalAlignment.Center };
        _views.SelectionChanged += (_, _) =>
        {
            if (_settingViews || _views.SelectedItem == null)
                return;
            int index = _views.Items.IndexOf(_views.SelectedItem);
            if (index >= 0)
                Safe(() => ViewChangedHandlers?.Invoke(this, index));
        };
        var top = new Grid { ColumnSpacing = 16, Padding = new Thickness(16, 10, 12, 4) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_heading);
        Grid.SetColumn(_views, 1);
        top.Children.Add(_views);

        // the picture with its labels, outlines and the tip
        _image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _labels = new Canvas { IsHitTestVisible = false };
        Brush accent = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? a) && a is Brush ab
            ? ab : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        _selected = new Polygon { Stroke = accent, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        _hover = new Polygon
        {
            Stroke = new SolidColorBrush(theme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 20, 20, 24)),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        var overlay = new Canvas { IsHitTestVisible = false };
        overlay.Children.Add(_hover);
        overlay.Children.Add(_selected);
        _tipText = new TextBlock { TextWrapping = TextWrapping.NoWrap, Tag = "NoLoc", FontSize = 12 };
        _tip = new Border
        {
            Child = _tipText,
            Padding = new Thickness(10, 6, 10, 7),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Background = ThemeBrush("AcrylicInAppFillColorDefaultBrush", "SolidBackgroundFillColorBaseBrush"),
            BorderBrush = ThemeBrush("SurfaceStrokeColorFlyoutBrush", "CardStrokeColorDefaultBrush"),
        };
        var tipLayer = new Canvas { IsHitTestVisible = false };
        tipLayer.Children.Add(_tip);

        _surface = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _surface.Children.Add(_image);
        _surface.Children.Add(_labels);
        _surface.Children.Add(overlay);
        _surface.Children.Add(tipLayer);
        _surface.SizeChanged += (_, _) =>
        {
            _resizeTimer!.Stop();
            _resizeTimer.Start();
        };
        _surface.PointerMoved += Surface_PointerMoved;
        _surface.PointerExited += (_, _) =>
        {
            _tip.Visibility = Visibility.Collapsed;
            Safe(() => PointerExited?.Invoke());
        };
        _surface.PointerPressed += Surface_PointerPressed;
        _surface.Tapped += (_, e) => Safe(() => Clicked?.Invoke(ToPixels(e.GetPosition(_surface))));
        _surface.DoubleTapped += (_, e) => Safe(() => DoubleClicked?.Invoke(ToPixels(e.GetPosition(_surface))));
        _surface.RightTapped += Surface_RightTapped;

        // the surface takes the keyboard focus (Backspace, Enter, Ctrl+C, ...)
        _focusHost = new UserControl { Content = _surface, IsTabStop = true, UseSystemFocusVisuals = false };
        var pictureBorder = new Border
        {
            Child = _focusHost,
            Margin = new Thickness(12, 4, 12, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
        };
        if (Application.Current.Resources.TryGetValue("VibePaneBorderBrush", out object? border) && border is Brush borderBrush)
            pictureBorder.BorderBrush = borderBrush;
        if (Application.Current.Resources.TryGetValue("VibePaneBackgroundBrush", out object? background) && background is Brush backgroundBrush)
            pictureBorder.Background = backgroundBrush;

        _legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var legendScroller = new ScrollViewer
        {
            Content = _legend,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(16, 8, 16, 0),
        };

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "NoLoc" };
        _progress = new ProgressBar { Width = 180, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var bottom = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 8, 12, 12) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        bottom.Children.Add(_progress);
        Grid.SetColumn(_buttonPanel, 2);
        bottom.Children.Add(_buttonPanel);

        var root = new Grid { RequestedTheme = theme };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(pictureBorder, 1);
        root.Children.Add(pictureBorder);
        Grid.SetRow(legendScroller, 2);
        root.Children.Add(legendScroller);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        root.KeyDown += Root_KeyDown;
        Content = root;

        _resizeTimer = _dispatcher.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(120);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => UpdateSize(raise: true);
        root.Loaded += (_, _) =>
        {
            UpdateSize(raise: true);
            _focusHost.Focus(FocusState.Programmatic);
        };

        Closed += (_, _) =>
        {
            IsClosed = true;
            _resizeTimer.Stop();
            _bitmap = null;
            Safe(() => ClosedHandlers?.Invoke(this, EventArgs.Empty));
            MemoryCleanup.Schedule();
        };
    }

    private static Brush? ThemeBrush(string key, string fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush ? brush
        : Application.Current.Resources.TryGetValue(fallback, out object? other) ? other as Brush : null;

    private void UpdateSize(bool raise)
    {
        if (IsClosed || _surface.XamlRoot == null)
            return;
        double scale = _surface.XamlRoot.RasterizationScale;
        int width = Math.Max(1, (int)Math.Round(_surface.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Round(_surface.ActualHeight * scale));
        if (width == _pixelWidth && height == _pixelHeight && scale == _scale && _bitmap != null)
            return;
        _scale = scale;
        _pixelWidth = width;
        _pixelHeight = height;
        if (raise)
            Safe(() => ResizedHandlers?.Invoke(this, EventArgs.Empty));
    }

    private PluginPoint ToPixels(Windows.Foundation.Point p) => new(p.X * _scale, p.Y * _scale);

    private static void Safe(Action action)
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

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() =>
            {
                if (!IsClosed)
                    action();
            });
    }

    // ---- IPluginCanvasWindow ----------------------------------------------------------------------

    public int PixelWidth => _pixelWidth;

    public int PixelHeight => _pixelHeight;

    public double Scale => _scale;

    public bool IsDark => _theme == ElementTheme.Dark;

    private EventHandler? ResizedHandlers;

    public event EventHandler? Resized
    {
        add => ResizedHandlers += value;
        remove => ResizedHandlers -= value;
    }

    private EventHandler<int>? ViewChangedHandlers;

    public event EventHandler<int>? ViewChanged
    {
        add => ViewChangedHandlers += value;
        remove => ViewChangedHandlers -= value;
    }

    private EventHandler? ClosedHandlers;

    event EventHandler? IPluginCanvasWindow.Closed
    {
        add => ClosedHandlers += value;
        remove => ClosedHandlers -= value;
    }

    public bool IsClosed { get; private set; }

    void IPluginCanvasWindow.Close() => OnUi(() =>
    {
        if (!IsClosed)
            Close();
    });

    public void SetPicture(int width, int height, byte[] pixels, IReadOnlyList<PluginLabel> labels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            return;
        byte[] copy = (byte[])pixels.Clone();
        var labelCopy = labels.ToList();
        OnUi(() =>
        {
            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                _bitmap = new WriteableBitmap(width, height);
            using (Stream stream = _bitmap.PixelBuffer.AsStream())
                stream.Write(copy, 0, width * height * 4);
            _bitmap.Invalidate();
            _image.Source = _bitmap;
            _image.Width = width / _scale;
            _image.Height = height / _scale;
            ShowLabels(labelCopy);
        });
    }

    private void ShowLabels(List<PluginLabel> labels)
    {
        _labels.Children.Clear();
        foreach (PluginLabel label in labels)
        {
            double width = label.Width / _scale, height = label.Height / _scale;
            if (width < 24 || height < label.FontSize * 1.25 || string.IsNullOrEmpty(label.Text))
                continue;
            var text = new TextBlock
            {
                Text = label.Text,
                Tag = "NoLoc",
                FontSize = label.FontSize,
                FontWeight = label.Bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(ToColor(label.Color)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = label.Text.Contains('\n') ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextAlignment = label.Centered ? TextAlignment.Center : TextAlignment.Left,
                MaxLines = Math.Max(1, (int)(height / (label.FontSize * 1.35))),
            };
            FrameworkElement element = text;
            if (label.Background != 0)
            {
                element = new Border
                {
                    Child = text,
                    Background = new SolidColorBrush(ToColor(label.Background)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MaxWidth = width,
                };
            }
            else
            {
                text.MaxWidth = width;
                if (label.Centered)
                    text.Width = width;
            }
            element.MaxHeight = height;
            if (label.Centered)
            {
                // the whole box, with the text in its middle
                var box = new Grid { Width = width, Height = height };
                element.HorizontalAlignment = HorizontalAlignment.Center;
                element.VerticalAlignment = VerticalAlignment.Center;
                box.Children.Add(element);
                element = box;
            }
            Canvas.SetLeft(element, label.X / _scale);
            Canvas.SetTop(element, label.Y / _scale);
            _labels.Children.Add(element);
        }
    }

    private static Windows.UI.Color ToColor(uint argb) =>
        Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public void SetHighlight(IReadOnlyList<PluginPoint>? hover, IReadOnlyList<PluginPoint>? selected)
    {
        PluginPoint[]? h = hover?.ToArray(), s = selected?.ToArray();
        OnUi(() =>
        {
            Show(_hover, h);
            Show(_selected, s);
        });
    }

    private void Show(Polygon polygon, PluginPoint[]? points)
    {
        if (points == null || points.Length < 2)
        {
            polygon.Visibility = Visibility.Collapsed;
            return;
        }
        var collection = new PointCollection();
        foreach (PluginPoint p in points)
            collection.Add(new Windows.Foundation.Point(p.X / _scale, p.Y / _scale));
        polygon.Points = collection;
        polygon.Visibility = Visibility.Visible;
    }

    public void SetHeading(string text) => OnUi(() => _heading.Text = text);

    public void SetViews(IReadOnlyList<string> names, int selected)
    {
        var list = names.ToList();
        OnUi(() =>
        {
            _settingViews = true;
            try
            {
                bool same = _views.Items.Count == list.Count &&
                            _views.Items.Select(i => i.Text).SequenceEqual(list);
                if (!same)
                {
                    _views.Items.Clear();
                    foreach (string name in list)
                        _views.Items.Add(new SelectorBarItem { Text = name, Tag = "NoLoc" });
                }
                if (selected >= 0 && selected < _views.Items.Count)
                    _views.SelectedItem = _views.Items[selected];
            }
            finally
            {
                _settingViews = false;
            }
        });
    }

    public void SetLegend(IReadOnlyList<PluginLegendItem> items)
    {
        var list = items.ToList();
        OnUi(() =>
        {
            _legend.Children.Clear();
            foreach (PluginLegendItem item in list)
            {
                var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                entry.Children.Add(new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(ToColor(item.Color | 0xFF000000)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                entry.Children.Add(new TextBlock { Text = item.Text, Tag = "NoLoc", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                _legend.Children.Add(entry);
            }
        });
    }

    public void SetStatus(string text) => OnUi(() => _status.Text = text);

    public void SetProgress(double? value) => OnUi(() =>
    {
        _progress.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = value is < 0;
        if (value is double v && v >= 0)
            _progress.Value = Math.Clamp(v, 0, 1);
    });

    public void SetButtons(IReadOnlyList<PluginButton> buttons)
    {
        var list = buttons.ToList();
        OnUi(() =>
        {
            _buttonPanel.Children.Clear();
            _buttons.Clear();
            foreach (PluginButton definition in list)
            {
                var button = new Button { Content = definition.Text, MinWidth = 96 };
                if (definition.IsDefault && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent) && accent is Style accentStyle)
                    button.Style = accentStyle;
                PluginButton d = definition;
                button.Click += (_, _) => Safe(d.Click);
                _buttons[definition.Id] = button;
                _buttonPanel.Children.Add(button);
            }
        });
    }

    public void UpdateButton(string id, string? text = null, bool? enabled = null) => OnUi(() =>
    {
        if (!_buttons.TryGetValue(id, out Button? button))
            return;
        if (text != null)
            button.Content = text;
        if (enabled != null)
            button.IsEnabled = enabled.Value;
    });

    public Func<PluginPoint, string?>? ToolTip { get; set; }

    public Action<PluginPoint>? PointerMoved { get; set; }

    public Action? PointerExited { get; set; }

    public Action<PluginPoint>? Clicked { get; set; }

    public Action<PluginPoint>? DoubleClicked { get; set; }

    public Func<PluginPoint, IReadOnlyList<PluginMenuItem>>? ContextMenu { get; set; }

    public Func<string, bool>? KeyPressed { get; set; }

    // ---- pointer, menu and keys ---------------------------------------------------------------------

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point position = e.GetCurrentPoint(_surface).Position;
        PluginPoint p = ToPixels(position);
        Safe(() => PointerMoved?.Invoke(p));
        string? tip = null;
        Safe(() => tip = ToolTip?.Invoke(p));
        if (string.IsNullOrEmpty(tip))
        {
            _tip.Visibility = Visibility.Collapsed;
            return;
        }
        if (_tipText.Text != tip)
            _tipText.Text = tip;
        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Windows.Foundation.Size size = _tip.DesiredSize;
        // beside the pointer, kept inside the picture
        double x = position.X + 16, y = position.Y + 20;
        if (x + size.Width > _surface.ActualWidth)
            x = Math.Max(0, position.X - 12 - size.Width);
        if (y + size.Height > _surface.ActualHeight)
            y = Math.Max(0, position.Y - 12 - size.Height);
        Canvas.SetLeft(_tip, x);
        Canvas.SetTop(_tip, y);
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _focusHost.Focus(FocusState.Pointer);
        PointerPointProperties properties = e.GetCurrentPoint(_surface).Properties;
        if (properties.IsXButton1Pressed)
        {
            // the mouse's back button
            Safe(() => KeyPressed?.Invoke("Backspace"));
            e.Handled = true;
        }
    }

    private void Surface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ContextMenu == null)
            return;
        Windows.Foundation.Point position = e.GetPosition(_surface);
        IReadOnlyList<PluginMenuItem> items = Array.Empty<PluginMenuItem>();
        Safe(() => items = ContextMenu?.Invoke(ToPixels(position)) ?? Array.Empty<PluginMenuItem>());
        var menu = new MenuFlyout();
        foreach (PluginMenuItem item in items)
        {
            var entry = new MenuFlyoutItem { Text = item.Text, IsEnabled = item.IsEnabled, Tag = "NoLoc" };
            if (!string.IsNullOrEmpty(item.ShortcutText))
                entry.KeyboardAcceleratorTextOverride = item.ShortcutText;
            PluginMenuItem i = item;
            entry.Click += (_, _) => Safe(i.Execute);
            menu.Items.Add(entry);
        }
        _tip.Visibility = Visibility.Collapsed;
        if (menu.Items.Count > 0)
            menu.ShowAt(_surface, position);
        e.Handled = true;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
            return;
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        string keys = KeyText(e.Key);
        if (keys.Length == 0 || KeyPressed == null)
            return;
        bool used = false;
        Safe(() => used = KeyPressed(keys));
        e.Handled = used;
    }

    private static string KeyText(VirtualKey key)
    {
        string name = key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (key - VirtualKey.A))).ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => ((char)('0' + (key - VirtualKey.NumberPad0))).ToString(),
            >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (key - VirtualKey.F1 + 1),
            VirtualKey.Back => "Backspace",
            VirtualKey.Enter => "Enter",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Space => "Space",
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
}
