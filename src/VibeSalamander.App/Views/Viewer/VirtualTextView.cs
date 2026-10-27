// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace VibeSalamander.Views.Viewer;

/// <summary>The rows a <see cref="VirtualTextView"/> shows: lines of text, or the hex view's rows.</summary>
internal interface IRowSource
{
    /// <summary>Rows known so far (it grows while a large file is still being scanned).</summary>
    long RowCount { get; }

    IReadOnlyList<string> Rows(long first, int count);

    /// <summary>The row goes on in the next one (a long line in pieces, or wrapped): copied without a line break.</summary>
    bool ContinuesNext(long row);

    /// <summary>The line number shown beside a row (null: none, e.g. a continuation).</summary>
    string? Label(long row);
}

/// <summary>A position in the view: a row and a column (in characters).</summary>
internal readonly record struct RowPosition(long Row, int Column) : IComparable<RowPosition>
{
    public int CompareTo(RowPosition other) => Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);
}

/// <summary>
/// Shows rows of monospaced text from an <see cref="IRowSource"/> of any length: only the rows on screen exist, the
/// scroll bars count rows and columns. Mouse and keys scroll, select (and copy) text, and a found match is highlighted.
/// </summary>
internal sealed class VirtualTextView : UserControl
{
    private const double GutterGap = 12;

    private readonly Grid _root = new();
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, SmallChange = 1 };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, SmallChange = 3 };
    private readonly List<TextBlock> _rows = new();
    private readonly List<TextBlock> _labels = new();
    private readonly Border _gutterLine = new() { Width = 1, Opacity = 0.25 };

    private IRowSource? _source;
    private long _top;                 // first row shown
    private int _left;                 // first column shown
    private double _charWidth = 8, _rowHeight = 18;
    private double _gutterWidth;
    private int _longest;              // the longest row seen (for the horizontal scroll bar)
    private bool _updating;
    private RowPosition? _anchor, _caret;
    private (long Row, int Column, int Length)? _match;
    private bool _selecting;

    public VirtualTextView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(_canvas);
        Grid.SetColumn(_vertical, 1);
        _root.Children.Add(_vertical);
        Grid.SetRow(_horizontal, 1);
        _root.Children.Add(_horizontal);
        _canvas.Children.Add(_gutterLine);
        Content = _root;

        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New");
        FontSize = 13;
        _canvas.SizeChanged += (_, _) => Layout();
        _vertical.ValueChanged += (_, e) =>
        {
            if (!_updating)
                ScrollTo((long)Math.Round(e.NewValue), _left);
        };
        _horizontal.ValueChanged += (_, e) =>
        {
            if (!_updating)
                ScrollTo(_top, (int)Math.Round(e.NewValue));
        };
        _canvas.PointerWheelChanged += Canvas_PointerWheelChanged;
        _canvas.PointerPressed += Canvas_PointerPressed;
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += (_, e) =>
        {
            _selecting = false;
            _canvas.ReleasePointerCapture(e.Pointer);
        };
        KeyDown += View_KeyDown;
        ActualThemeChanged += (_, _) => Render();
    }

    /// <summary>The first row on screen changed (for the status line).</summary>
    public event EventHandler? Scrolled;

    /// <summary>The selection changed.</summary>
    public event EventHandler? SelectionChanged;

    public long TopRow => _top;

    public int VisibleRows => Math.Max(1, (int)(_canvas.ActualHeight / _rowHeight));

    /// <summary>Characters that fit in a row (for wrapping).</summary>
    public int VisibleColumns => Math.Max(10, (int)((_canvas.ActualWidth - _gutterWidth) / _charWidth) - 1);

    /// <summary>Line numbers beside the rows.</summary>
    public bool ShowLabels { get; set; } = true;

    public IRowSource? Source
    {
        get => _source;
        set
        {
            _source = value;
            _top = 0;
            _left = 0;
            _longest = 0;
            _anchor = _caret = null;
            _match = null;
            Layout();
        }
    }

    /// <summary>The text size (Ctrl+wheel, Ctrl+plus / minus).</summary>
    public double TextSize
    {
        get => FontSize;
        set
        {
            FontSize = Math.Clamp(value, 8, 40);
            Layout();
        }
    }

    // ---- layout and drawing --------------------------------------------------------------------------------

    /// <summary>Measures the font, makes as many row elements as fit, and draws.</summary>
    public void Layout()
    {
        var probe = new TextBlock { Text = new string('M', 40), FontFamily = FontFamily, FontSize = FontSize };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _charWidth = Math.Max(1, probe.DesiredSize.Width / 40);
        _rowHeight = Math.Max(8, Math.Ceiling(probe.DesiredSize.Height));

        int needed = (int)Math.Ceiling(_canvas.ActualHeight / _rowHeight) + 1;
        while (_rows.Count < needed)
        {
            var row = new TextBlock { FontFamily = FontFamily, IsHitTestVisible = false, TextWrapping = TextWrapping.NoWrap };
            var label = new TextBlock { FontFamily = FontFamily, IsHitTestVisible = false, TextAlignment = TextAlignment.Right, Opacity = 0.5 };
            _rows.Add(row);
            _labels.Add(label);
            _canvas.Children.Add(row);
            _canvas.Children.Add(label);
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Visibility = i < needed ? Visibility.Visible : Visibility.Collapsed;
            _labels[i].Visibility = i < needed ? Visibility.Visible : Visibility.Collapsed;
            _rows[i].FontSize = _labels[i].FontSize = FontSize;
            _rows[i].FontFamily = _labels[i].FontFamily = FontFamily;
        }
        _canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, _canvas.ActualWidth), Math.Max(0, _canvas.ActualHeight)) };
        Refresh();
    }

    /// <summary>Draws again (rows may have been added: a large file still being scanned).</summary>
    public void Refresh()
    {
        if (_source == null)
            return;
        long count = _source.RowCount;
        _top = Math.Clamp(_top, 0, Math.Max(0, count - 1));
        Render();
    }

    private void Render()
    {
        if (_source == null)
        {
            foreach (TextBlock row in _rows)
                row.Text = string.Empty;
            foreach (TextBlock label in _labels)
                label.Text = string.Empty;
            return;
        }
        long count = _source.RowCount;
        int visible = _rows.Count(r => r.Visibility == Visibility.Visible);
        IReadOnlyList<string> texts = _source.Rows(_top, visible);

        // the gutter fits the largest line number
        bool labels = ShowLabels && _source.Label(0) != null;
        int digits = labels ? Math.Max(3, (_source.Label(Math.Max(0, count - 1)) ?? string.Empty).Length) : 0;
        // a little slack: digits can be drawn a bit wider than the measured character
        double labelWidth = Math.Ceiling(digits * _charWidth * 1.08) + 6;
        _gutterWidth = labels ? labelWidth + GutterGap : 6;
        _gutterLine.Visibility = labels ? Visibility.Visible : Visibility.Collapsed;
        _gutterLine.Height = _canvas.ActualHeight;
        _gutterLine.Background = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        Canvas.SetLeft(_gutterLine, _gutterWidth - GutterGap / 2);

        var selectionBrush = (Brush)Application.Current.Resources["AccentFillColorSelectedTextBackgroundBrush"];
        var matchBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 0));
        for (int i = 0; i < visible; i++)
        {
            TextBlock row = _rows[i];
            TextBlock label = _labels[i];
            long index = _top + i;
            string text = i < texts.Count ? texts[i] : string.Empty;
            _longest = Math.Max(_longest, text.Length);
            // only the part from the first column shown (texts are monospaced)
            string shown = _left < text.Length ? text[_left..] : string.Empty;
            if (shown.Length > 2000)
                shown = shown[..2000];
            row.Text = shown;
            row.TextHighlighters.Clear();
            Canvas.SetLeft(row, _gutterWidth);
            Canvas.SetTop(row, i * _rowHeight);
            row.Height = _rowHeight;

            label.Text = labels && i < texts.Count ? _source.Label(index) ?? string.Empty : string.Empty;
            label.Width = labels ? labelWidth : 0;
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, i * _rowHeight);

            if (i >= texts.Count)
                continue;
            if (SelectionOn(index, text.Length) is { } selected)
                Highlight(row, selected.Start - _left, selected.Length, selectionBrush, null);
            if (_match is { } m && m.Row == index)
                Highlight(row, m.Column - _left, m.Length, matchBrush, new SolidColorBrush(Colors.Black));
        }

        _updating = true;
        _vertical.Maximum = Math.Max(0, count - 1);
        _vertical.ViewportSize = visible;
        _vertical.LargeChange = Math.Max(1, visible - 1);
        _vertical.Value = _top;
        int columns = Math.Max(1, (int)((_canvas.ActualWidth - _gutterWidth) / _charWidth));
        _horizontal.Maximum = Math.Max(0, _longest - columns + 1);
        _horizontal.ViewportSize = columns;
        _horizontal.LargeChange = Math.Max(1, columns - 4);
        _horizontal.Value = _left;
        _horizontal.Visibility = _longest > columns || _left > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
    }

    private static void Highlight(TextBlock row, int start, int length, Brush background, Brush? foreground)
    {
        int from = Math.Max(0, start);
        int to = Math.Min(row.Text.Length, start + length);
        if (to <= from)
            return;
        var highlighter = new TextHighlighter { Background = background };
        if (foreground != null)
            highlighter.Foreground = foreground;
        highlighter.Ranges.Add(new TextRange { StartIndex = from, Length = to - from });
        row.TextHighlighters.Add(highlighter);
    }

    // ---- scrolling ----------------------------------------------------------------------------------------

    public void ScrollTo(long row, int column)
    {
        if (_source == null)
            return;
        long max = Math.Max(0, _source.RowCount - 1);
        long top = Math.Clamp(row, 0, max);
        int left = Math.Max(0, column);
        if (top == _top && left == _left)
            return;
        _top = top;
        _left = left;
        Render();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Brings a row (and a column range) into view, near the top third when it has to scroll.</summary>
    public void Reveal(long row, int column = 0, int length = 0)
    {
        int visible = VisibleRows;
        long top = _top;
        if (row < _top || row >= _top + visible - 1)
            top = Math.Max(0, row - visible / 3);
        int columns = Math.Max(1, (int)((_canvas.ActualWidth - _gutterWidth) / _charWidth));
        int left = _left;
        if (column < _left || column + length > _left + columns)
            left = Math.Max(0, column - columns / 4);
        if (top == _top && left == _left)
            Render();
        else
            ScrollTo(top, left);
    }

    /// <summary>Highlights a found match (and scrolls to it); null removes the highlight.</summary>
    public void ShowMatch(long row, int column, int length)
    {
        _match = (row, column, length);
        _anchor = new RowPosition(row, column);
        _caret = new RowPosition(row, column + length);
        Reveal(row, column, length);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearMatch()
    {
        _match = null;
        Render();
    }

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        if (KeyboardState.IsDown(KeyboardState.Control))
            TextSize += delta > 0 ? 1 : -1;
        else if (KeyboardState.IsDown(KeyboardState.Shift) || e.GetCurrentPoint(_canvas).Properties.IsHorizontalMouseWheel)
            ScrollTo(_top, _left + (delta > 0 ? -6 : 6) * (e.GetCurrentPoint(_canvas).Properties.IsHorizontalMouseWheel ? -1 : 1));
        else
            ScrollTo(_top - delta / 40, _left);
        e.Handled = true;
    }

    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_source == null)
            return;
        bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
        int page = Math.Max(1, VisibleRows - 1);
        switch (e.Key)
        {
            case VirtualKey.Up: ScrollTo(_top - 1, _left); break;
            case VirtualKey.Down: ScrollTo(_top + 1, _left); break;
            case VirtualKey.PageUp: ScrollTo(_top - page, _left); break;
            case VirtualKey.PageDown: ScrollTo(_top + page, _left); break;
            case VirtualKey.Home: ScrollTo(ctrl ? 0 : _top, 0); break;
            case VirtualKey.End when ctrl: ScrollTo(_source.RowCount - VisibleRows + 1, 0); break;
            case VirtualKey.End: ScrollTo(_top, Math.Max(0, _longest - 20)); break;
            case VirtualKey.Left: ScrollTo(_top, _left - (ctrl ? 20 : 4)); break;
            case VirtualKey.Right: ScrollTo(_top, _left + (ctrl ? 20 : 4)); break;
            case VirtualKey.A when ctrl: SelectAll(); break;
            case VirtualKey.C when ctrl: CopySelection(); break;
            case VirtualKey.Insert when ctrl: CopySelection(); break;
            case (VirtualKey)187 when ctrl: TextSize += 1; break; // "+"
            case (VirtualKey)189 when ctrl: TextSize -= 1; break; // "-"
            case VirtualKey.Add when ctrl: TextSize += 1; break;
            case VirtualKey.Subtract when ctrl: TextSize -= 1; break;
            case VirtualKey.Number0 when ctrl: TextSize = 13; break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- selection and copying -----------------------------------------------------------------------------

    private RowPosition PositionAt(Windows.Foundation.Point point)
    {
        long row = _top + (long)Math.Floor(point.Y / _rowHeight);
        int column = _left + (int)Math.Round((point.X - _gutterWidth) / _charWidth);
        long max = Math.Max(0, (_source?.RowCount ?? 1) - 1);
        return new RowPosition(Math.Clamp(row, 0, max), Math.Max(0, column));
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        PointerPoint point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed || _source == null)
            return;
        RowPosition at = PositionAt(point.Position);
        if (KeyboardState.IsDown(KeyboardState.Shift) && _anchor != null)
            _caret = at;
        else
            _anchor = _caret = at;
        _selecting = true;
        _canvas.CapturePointer(e.Pointer);
        Render();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
            return;
        Windows.Foundation.Point p = e.GetCurrentPoint(_canvas).Position;
        _caret = PositionAt(p);
        // dragging beyond the edges scrolls
        if (p.Y < 0)
            ScrollTo(_top - 1, _left);
        else if (p.Y > _canvas.ActualHeight)
            ScrollTo(_top + 1, _left);
        Render();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private (RowPosition Start, RowPosition End)? Selection =>
        _anchor is { } a && _caret is { } c && a.CompareTo(c) != 0 ? (a.CompareTo(c) < 0 ? (a, c) : (c, a)) : null;

    public bool HasSelection => Selection != null;

    /// <summary>Where the selection (or the click) is: the row the caret is on.</summary>
    public RowPosition? Caret => _caret;

    private (int Start, int Length)? SelectionOn(long row, int length)
    {
        if (Selection is not { } s || row < s.Start.Row || row > s.End.Row)
            return null;
        int start = row == s.Start.Row ? s.Start.Column : 0;
        int end = row == s.End.Row ? s.End.Column : length + 1;
        return end > start ? (start, end - start) : null;
    }

    public void SelectAll()
    {
        if (_source == null || _source.RowCount == 0)
            return;
        _anchor = new RowPosition(0, 0);
        _caret = new RowPosition(_source.RowCount - 1, int.MaxValue / 2);
        Render();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The selected text (at most about 64 MB; <paramref name="cut"/> says when it was more).</summary>
    public string SelectedText(out bool cut)
    {
        cut = false;
        if (_source == null || Selection is not { } s)
            return string.Empty;
        var text = new StringBuilder();
        const int block = 512;
        for (long row = s.Start.Row; row <= s.End.Row; row += block)
        {
            int count = (int)Math.Min(block, s.End.Row - row + 1);
            IReadOnlyList<string> rows = _source.Rows(row, count);
            for (int i = 0; i < rows.Count; i++)
            {
                long r = row + i;
                string line = rows[i];
                int start = r == s.Start.Row ? Math.Min(s.Start.Column, line.Length) : 0;
                int end = r == s.End.Row ? Math.Min(s.End.Column, line.Length) : line.Length;
                if (end > start)
                    text.Append(line, start, end - start);
                if (r < s.End.Row && !_source.ContinuesNext(r))
                    text.Append("\r\n");
                if (text.Length > 64 * 1024 * 1024)
                {
                    cut = true;
                    return text.ToString();
                }
            }
        }
        return text.ToString();
    }

    /// <summary>Raised after a copy that had to be cut short.</summary>
    public event EventHandler? CopyCut;

    public void CopySelection()
    {
        string text = SelectedText(out bool cut);
        if (text.Length == 0)
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        if (cut)
            CopyCut?.Invoke(this, EventArgs.Empty);
    }
}
