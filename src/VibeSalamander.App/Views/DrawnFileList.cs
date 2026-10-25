// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Windows.Foundation;
using Color = Windows.UI.Color;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>
/// The Detailed view drawn by hand (Win2D) instead of a ListView: one surface, only the visible rows are drawn,
/// no row controls are created, recycled or measured. Configuration → Panels → Drawn file list (experimental).
/// It is only the picture: the panel (FilePane) keeps the caret, marking, menus, rename and drag & drop, and asks
/// this list where rows are.
/// </summary>
public sealed partial class DrawnFileList : UserControl
{
    /// <summary>Space above the first row (the ListView's padding).</summary>
    public const double TopPadding = 2;

    /// <summary>Where the row content starts (the header's padding): the icon, then the name after 22.</summary>
    private const double ContentLeft = 16, IconColumn = 22, RightPadding = 4;

    private readonly Grid _root;
    private readonly CanvasControl _canvas;
    private readonly ScrollBar _scrollBar;
    private readonly Canvas _overlay;
    private readonly DispatcherTimer _hideScrollBar;

    private PaneViewModel? _pane;
    private Services.IconCache? _icons;
    private IReadOnlyList<FileItemViewModel> _items = Array.Empty<FileItemViewModel>();
    private double _offset;
    private int _hover = -1;
    private int _pendingScrollIndex = -1;
    private bool _drawing;
    private bool _pointerOnScrollBar;

    private readonly HashSet<FileItemViewModel> _watched = new();
    private ConditionalWeakTable<ImageSource, CanvasBitmap> _bitmaps = new();
    private readonly Dictionary<int, double> _nameRight = new();

    private CanvasTextFormat? _left, _right, _glyphs;
    private string? _formatKey;

    private TextBox? _editor;
    private int _editorIndex = -1;

    private DrawnFileList()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        _canvas = new CanvasControl { ClearColor = Colors.Transparent };
        _canvas.Draw += Canvas_Draw;
        _canvas.CreateResources += (_, e) =>
        {
            if (e.Reason != CanvasCreateResourcesReason.FirstTime)
                _bitmaps = new ConditionalWeakTable<ImageSource, CanvasBitmap>(); // a new device (driver update, GPU reset)
        };
        _scrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            IndicatorMode = ScrollingIndicatorMode.None,
            Minimum = 0,
            IsTabStop = false,
        };
        _scrollBar.ValueChanged += (_, e) =>
        {
            if (Math.Abs(e.NewValue - _offset) > 0.01)
                ScrollTo(e.NewValue);
        };
        _scrollBar.PointerEntered += (_, _) =>
        {
            _pointerOnScrollBar = true;
            ShowScrollBar();
        };
        _scrollBar.PointerExited += (_, _) =>
        {
            _pointerOnScrollBar = false;
            ShowScrollBar();
        };
        _overlay = new Canvas { IsHitTestVisible = true };
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlay);
        _root.Children.Add(_scrollBar);
        Content = _root;

        // like the ListView's scrollbar: shown while the mouse moves or the list scrolls, hidden after a pause
        _hideScrollBar = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _hideScrollBar.Tick += (_, _) =>
        {
            if (_pointerOnScrollBar)
                return;
            _hideScrollBar.Stop();
            _scrollBar.IndicatorMode = ScrollingIndicatorMode.None;
        };

        AddHandler(PointerMovedEvent, new PointerEventHandler(List_PointerMoved), handledEventsToo: true);
        _canvas.PointerExited += (_, _) => SetHover(-1);
        _canvas.PointerWheelChanged += Canvas_PointerWheelChanged;
        SizeChanged += (_, _) => UpdateScrollRange();
        ActualThemeChanged += (_, _) => Invalidate();
    }

    /// <summary>The list, or null when Win2D can't run here (the ListView is used then).</summary>
    public static DrawnFileList? TryCreate()
    {
        try
        {
            return new DrawnFileList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The drawing surface: clicks, taps and drags on the rows arrive here (not the scrollbar or the rename box).</summary>
    public UIElement Surface => _canvas;

    // ---- the panel -----------------------------------------------------------------------------

    public void Attach(PaneViewModel pane, Services.IconCache icons)
    {
        if (ReferenceEquals(_pane, pane))
        {
            Refresh();
            return;
        }
        Detach();
        _pane = pane;
        _icons = icons;
        pane.PropertyChanged += Pane_PropertyChanged;
        pane.Columns.PropertyChanged += Columns_PropertyChanged;
        Refresh();
    }

    public void Detach()
    {
        if (_pane != null)
        {
            _pane.PropertyChanged -= Pane_PropertyChanged;
            _pane.Columns.PropertyChanged -= Columns_PropertyChanged;
        }
        _pane = null;
        Unwatch();
        _items = Array.Empty<FileItemViewModel>();
    }

    private void Refresh()
    {
        if (_pane != null && !ReferenceEquals(_items, _pane.Items))
        {
            Unwatch();
            _items = _pane.Items;
        }
        UpdateScrollRange();
        Invalidate();
    }

    private void Pane_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.Items) or nameof(PaneViewModel.ItemsSource))
            Refresh();
        else
            Invalidate(); // caret, marks, sizes in the information line: cheap, only the visible rows are drawn
    }

    private void Columns_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Invalidate();

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_drawing)
            Invalidate();
    }

    private void Unwatch()
    {
        foreach (FileItemViewModel item in _watched)
            item.PropertyChanged -= Item_PropertyChanged;
        _watched.Clear();
    }

    /// <summary>Fonts, colors or the row height changed (Configuration).</summary>
    public void ApplyOptions()
    {
        _formatKey = null;
        _root.Background = UiState.PanelBackground ?? new SolidColorBrush(Colors.Transparent);
        UpdateScrollRange();
        Invalidate();
    }

    public void Invalidate()
    {
        _canvas.Invalidate();
        UpdateEditor();
    }

    // ---- geometry ------------------------------------------------------------------------------

    private static double RowHeight => Math.Max(1, UiState.RowHeight);

    public double ViewportHeight => _canvas.ActualHeight;

    /// <summary>Rows that fit completely (PgUp / PgDn move by this less one).</summary>
    public int VisibleRows => Math.Max(1, (int)(ViewportHeight / RowHeight));

    private double Extent => TopPadding + _items.Count * RowHeight + 2;

    private double MaxOffset => Math.Max(0, Extent - ViewportHeight);

    /// <summary>The row's rectangle in this control's coordinates (it may be outside the visible part).</summary>
    public Rect RowBounds(int index) => new(0, TopPadding + index * RowHeight - _offset, Math.Max(0, ActualWidth), RowHeight);

    /// <summary>The item under a point of this control, or -1 (the empty part below the items).</summary>
    public int IndexAt(Point point)
    {
        if (point.Y < 0 || point.Y > ViewportHeight)
            return -1;
        double y = point.Y + _offset - TopPadding;
        if (y < 0)
            return -1;
        int index = (int)(y / RowHeight);
        return index < _items.Count ? index : -1;
    }

    /// <summary>True when the point is on the name text of the row (a slow second click there renames).</summary>
    public bool IsOnName(Point point, int index)
    {
        if (!_nameRight.TryGetValue(index, out double right))
            return false;
        double left = ContentLeft + IconColumn;
        return point.X >= left && point.X <= right;
    }

    /// <summary>True when the row is completely visible.</summary>
    public bool IsRowShown(int index)
    {
        if (index < 0 || index >= _items.Count || ViewportHeight <= 0)
            return false;
        Rect row = RowBounds(index);
        return row.Top >= 0 && row.Bottom <= ViewportHeight + 0.5;
    }

    private record struct Columns(double NameLeft, double NameWidth, double Ext, double ExtW, double Size, double SizeW,
        double Date, double DateW, double Time, double TimeW, double Attr, double AttrW);

    private Columns Layout()
    {
        ColumnLayout? c = _pane?.Columns;
        double W(DetailColumn column) => c != null && c.IsVisible(column) ? c.GetWidth(column) : 0;
        double right = Math.Max(0, ActualWidth) - RightPadding;
        double attrW = W(DetailColumn.Attr), timeW = W(DetailColumn.Time), dateW = W(DetailColumn.Date);
        double sizeW = W(DetailColumn.Size), extW = W(DetailColumn.Ext);
        double attr = right - attrW;
        double time = attr - timeW;
        double date = time - dateW;
        double size = date - sizeW;
        double ext = size - extW;
        double nameLeft = ContentLeft + IconColumn;
        return new Columns(nameLeft, Math.Max(0, ext - nameLeft), ext, extW, size, sizeW, date, dateW, time, timeW, attr, attrW);
    }

    // ---- scrolling -----------------------------------------------------------------------------

    public void ResetScroll() => ScrollTo(0);

    public void ScrollTo(double offset)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        offset = Math.Round(Math.Clamp(offset, 0, MaxOffset) * scale) / scale; // whole pixels: sharp text
        if (Math.Abs(offset - _offset) < 0.001 && Math.Abs(_scrollBar.Value - offset) < 0.001)
            return;
        _offset = offset;
        _scrollBar.Value = offset;
        Invalidate();
    }

    /// <summary>Scrolls just enough to show the whole row (like ListView.ScrollIntoView).</summary>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;
        if (ViewportHeight <= 0)
        {
            _pendingScrollIndex = index; // not laid out yet: when it is
            return;
        }
        Rect row = RowBounds(index);
        if (row.Top < 0)
            ScrollTo(_offset + row.Top - (index == 0 ? TopPadding : 0));
        else if (row.Bottom > ViewportHeight)
            ScrollTo(_offset + row.Bottom - ViewportHeight + (index == _items.Count - 1 ? 2 : 0));
    }

    private void UpdateScrollRange()
    {
        double viewport = Math.Max(0, ViewportHeight);
        _scrollBar.Maximum = MaxOffset;
        _scrollBar.ViewportSize = viewport;
        _scrollBar.LargeChange = Math.Max(RowHeight, viewport - RowHeight);
        _scrollBar.SmallChange = RowHeight;
        _scrollBar.Visibility = MaxOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_offset > MaxOffset)
            ScrollTo(MaxOffset);
        if (_pendingScrollIndex >= 0 && viewport > 0)
        {
            int index = _pendingScrollIndex;
            _pendingScrollIndex = -1;
            ScrollIntoView(index);
        }
        Invalidate();
    }

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        if (delta == 0 || MaxOffset <= 0)
            return;
        // three rows a notch (the Windows default)
        ScrollTo(_offset - delta / 120.0 * 3 * RowHeight);
        UpdateHover(e.GetCurrentPoint(this).Position);
        ShowScrollBar();
        e.Handled = true;
    }

    private void ShowScrollBar()
    {
        if (_scrollBar.Visibility != Visibility.Visible)
            return;
        _scrollBar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        _hideScrollBar.Stop();
        _hideScrollBar.Start();
    }

    // ---- hover ---------------------------------------------------------------------------------

    private void List_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            ShowScrollBar();
            if (!_pointerOnScrollBar)
                UpdateHover(e.GetCurrentPoint(this).Position);
        }
    }

    private void UpdateHover(Point point) => SetHover(IndexAt(point));

    private void SetHover(int index)
    {
        if (index == _hover)
            return;
        _hover = index;
        _canvas.Invalidate();
    }

    // ---- in-place rename -----------------------------------------------------------------------

    /// <summary>Puts the rename box over the row's name and extension (F2); it follows the row when the list scrolls.</summary>
    public void ShowEditor(TextBox box, int index)
    {
        HideEditor();
        _editor = box;
        _editorIndex = index;
        _overlay.Children.Add(box);
        UpdateEditor();
        Invalidate();
    }

    public void HideEditor()
    {
        if (_editor != null)
            _overlay.Children.Remove(_editor);
        _editor = null;
        _editorIndex = -1;
        _canvas.Invalidate();
    }

    private void UpdateEditor()
    {
        if (_editor == null)
            return;
        Columns c = Layout();
        Rect row = RowBounds(_editorIndex);
        double left = c.NameLeft - 3;
        double right = c.Ext + c.ExtW - 4;
        Canvas.SetLeft(_editor, left);
        Canvas.SetTop(_editor, row.Top + 1);
        _editor.Width = Math.Max(40, right - left);
        _editor.Height = Math.Max(10, row.Height - 2);
    }

    // ---- drawing -------------------------------------------------------------------------------

    private static Color ColorOf(Brush? brush, double opacity = 1)
    {
        if (brush is not SolidColorBrush solid)
            return Colors.Transparent;
        Color c = solid.Color;
        c.A = (byte)Math.Clamp(Math.Round(c.A * solid.Opacity * opacity), 0, 255);
        return c;
    }

    private static string FontFamilyName(FontFamily? family)
    {
        string? source = family?.Source;
        if (string.IsNullOrWhiteSpace(source) || source == "XamlAutoFontFamily")
        {
            if (Application.Current.Resources.TryGetValue("ContentControlThemeFontFamily", out object? value) &&
                value is FontFamily theme && !string.IsNullOrWhiteSpace(theme.Source) && theme.Source != "XamlAutoFontFamily")
                source = theme.Source;
            else
                source = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        }
        return source.Split(',')[0].Trim(); // DirectWrite takes one family (the first of a fallback list)
    }

    private static string[]? _systemFonts;

    private static bool HasFont(string name)
    {
        try
        {
            _systemFonts ??= CanvasTextFormat.GetSystemFontFamilies();
        }
        catch (Exception)
        {
            _systemFonts = Array.Empty<string>();
        }
        return _systemFonts.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureFormats()
    {
        string family = FontFamilyName(UiState.PanelFont);
        float size = (float)UiState.FontSize;
        string key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (key == _formatKey && _left != null)
            return;
        _formatKey = key;
        CanvasTextFormat Text(CanvasHorizontalAlignment alignment) => new()
        {
            FontFamily = family,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            HorizontalAlignment = alignment,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        _left = Text(CanvasHorizontalAlignment.Left);
        _right = Text(CanvasHorizontalAlignment.Right);
        _glyphs = new CanvasTextFormat
        {
            FontFamily = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets",
            FontSize = 14,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
    }

    private CanvasBitmap? BitmapOf(ICanvasResourceCreator creator, ImageSource? source)
    {
        if (source is not WriteableBitmap bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            return null;
        if (_bitmaps.TryGetValue(source, out CanvasBitmap? cached))
            return cached;
        CanvasBitmap? created = null;
        try
        {
            byte[] pixels = bitmap.PixelBuffer.ToArray(); // premultiplied BGRA (IconCache)
            created = CanvasBitmap.CreateFromBytes(creator, pixels, bitmap.PixelWidth, bitmap.PixelHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        }
        catch (Exception)
        {
            // drawn as the glyph instead
        }
        if (created != null)
            _bitmaps.AddOrUpdate(source, created);
        return created;
    }

    private Color _accent;
    private bool _accentDark;
    private bool _accentRead;

    private Color Accent(bool dark)
    {
        if (!_accentRead || _accentDark != dark)
        {
            _accentRead = true;
            _accentDark = dark;
            try
            {
                var settings = new Windows.UI.ViewManagement.UISettings();
                _accent = settings.GetColorValue(dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            }
            catch (Exception)
            {
                _accent = Color.FromArgb(255, 0x00, 0x5F, 0xB8);
            }
        }
        return _accent;
    }

    /// <summary>The accent color may have changed (Windows settings): read it again at the next drawing.</summary>
    public void RefreshAccent() => _accentRead = false;

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        _nameRight.Clear();
        if (_pane == null || _items.Count == 0 || ActualWidth <= 0)
        {
            Unwatch();
            return;
        }
        EnsureFormats();
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;

        double rowHeight = RowHeight;
        double width = ActualWidth;
        int first = Math.Max(0, (int)((_offset - TopPadding) / rowHeight));
        int last = Math.Min(_items.Count - 1, (int)((_offset + ViewportHeight - TopPadding) / rowHeight));
        bool dark = ActualTheme == ElementTheme.Dark;
        Color hover = dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x09, 0, 0, 0);
        Color caretHover = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0);
        Color? focus = UiState.FocusBackground is { } focusBrush ? ColorOf(focusBrush) : null;
        int caret = _pane.CaretIndex;
        bool simpleIcons = _pane.CurrentDriveOptions?.SimpleIcons == true;
        Columns c = Layout();

        // icons of the rows coming into view; watch them for changes (icon loaded, size computed, marked)
        _drawing = true;
        var visible = new HashSet<FileItemViewModel>();
        try
        {
            for (int i = first; i <= last; i++)
            {
                FileItemViewModel item = _items[i];
                visible.Add(item);
                if (!simpleIcons && _icons != null)
                    item.EnsureIcon(_icons);
            }
        }
        finally
        {
            _drawing = false;
        }
        foreach (FileItemViewModel gone in _watched.Where(w => !visible.Contains(w)).ToList())
        {
            gone.PropertyChanged -= Item_PropertyChanged;
            _watched.Remove(gone);
        }
        foreach (FileItemViewModel shown in visible)
            if (_watched.Add(shown))
                shown.PropertyChanged += Item_PropertyChanged;

        for (int i = first; i <= last; i++)
        {
            FileItemViewModel item = _items[i];
            double top = TopPadding + i * rowHeight - _offset;

            // the caret: a rounded bar with the accent pill (the Windows 11 list look); hover: a lighter bar
            var bar = new Rect(4, top + 1, Math.Max(0, width - 8), Math.Max(0, rowHeight - 2));
            if (i == caret)
            {
                ds.FillRoundedRectangle(bar, 4, 4, focus ?? (i == _hover ? caretHover : hover));
                double pill = Math.Clamp(rowHeight - 8, 6, 16);
                ds.FillRoundedRectangle(new Rect(4, top + (rowHeight - pill) / 2, 3, pill), 1.5f, 1.5f, Accent(dark));
            }
            else if (i == _hover)
            {
                ds.FillRoundedRectangle(bar, 4, 4, hover);
            }

            double opacity = item.ItemOpacity;
            // icon (or its glyph until the icon has loaded)
            if (BitmapOf(sender, item.Icon) is { } icon)
            {
                var target = new Rect(ContentLeft, top + Math.Round((rowHeight - 16) / 2), 16, 16);
                ds.DrawImage(icon, target, icon.Bounds, (float)opacity, CanvasImageInterpolation.HighQualityCubic);
            }
            else
            {
                ds.DrawText(item.Glyph, new Rect(ContentLeft, top, IconColumn, rowHeight), ColorOf(item.GlyphBrush, opacity), _glyphs);
            }

            if (!item.IsRenaming)
            {
                if (c.NameWidth > 0)
                {
                    using var name = new CanvasTextLayout(sender, item.Name, _left, (float)c.NameWidth, (float)rowHeight);
                    ds.DrawTextLayout(name, (float)c.NameLeft, (float)top, ColorOf(item.NameBrush, opacity));
                    _nameRight[i] = c.NameLeft + Math.Min(c.NameWidth, name.LayoutBounds.Right);
                }
                if (c.ExtW > 6)
                    ds.DrawText(item.Ext, new Rect(c.Ext + 6, top, c.ExtW - 6, rowHeight), ColorOf(item.TextBrush, opacity), _left);
            }
            Color text = ColorOf(item.TextBrush, opacity);
            if (c.SizeW > 8)
                ds.DrawText(item.SizeText, new Rect(c.Size, top, c.SizeW - 8, rowHeight), text, _right);
            if (c.DateW > 8)
                ds.DrawText(item.DateText, new Rect(c.Date, top, c.DateW - 8, rowHeight), text, _right);
            if (c.TimeW > 8)
                ds.DrawText(item.TimeText, new Rect(c.Time, top, c.TimeW - 8, rowHeight), text, _right);
            if (c.AttrW > 4)
                ds.DrawText(item.AttrText, new Rect(c.Attr + 4, top, c.AttrW - 4, rowHeight), text, _left);
        }
    }
}
