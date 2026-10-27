// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>
/// Selecting with a mouse rectangle in a panel's list, like Explorer: a left press on the empty area (or, in the
/// Detailed view, on the empty part of a row beside the name) and a move draws a rectangle that marks the rows it
/// touches; Ctrl flips their marks instead. The list scrolls while the mouse is beyond its edges. The rules are in
/// Core's MarqueeRules; this class follows the mouse and draws.
/// </summary>
internal sealed class MarqueeSelection
{
    private readonly ListView _list;
    private readonly Canvas _layer;
    private readonly FrameworkElement _rectangle;
    private readonly Func<PaneViewModel?> _pane;
    private readonly Func<ScrollViewer?> _scroller;
    private readonly Action _started;

    private bool _candidate, _active;
    private Windows.Foundation.Point _start;        // in ItemsPanelRoot coordinates (moves with the content)
    private Windows.Foundation.Point _lastPointer;  // in list coordinates
    private Pointer? _pointer;
    private bool _toggle;
    private HashSet<int> _markedAtStart = new();
    private DispatcherQueueTimer? _scrollTimer;

    /// <param name="list">The panel's file list.</param>
    /// <param name="layer">A canvas over the list the rectangle is drawn on.</param>
    /// <param name="rectangle">The rectangle (collapsed while not selecting).</param>
    /// <param name="pane">The panel's rows and marks.</param>
    /// <param name="scroller">The list's scroll viewer.</param>
    /// <param name="started">Called when a rectangle starts (the panel stops what a click would have started).</param>
    public MarqueeSelection(ListView list, Canvas layer, FrameworkElement rectangle, Func<PaneViewModel?> pane,
        Func<ScrollViewer?> scroller, Action started)
    {
        _list = list;
        _layer = layer;
        _rectangle = rectangle;
        _pane = pane;
        _scroller = scroller;
        _started = started;
        // moves and the release arrive handled when they start on an item
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(PointerMoved), handledEventsToo: true);
        list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PointerReleased), handledEventsToo: true);
        list.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CaptureLost), handledEventsToo: true);
    }

    /// <summary>A press that may become a rectangle, or a rectangle being drawn (an item drag must not start then).</summary>
    public bool IsBusy => _candidate || _active;

    /// <summary>A left press in the list (the panel calls it for every press).</summary>
    public void Pressed(PointerRoutedEventArgs e)
    {
        _candidate = false;
        PaneViewModel? pane = _pane();
        if (pane == null || pane.IsThisPc || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            _list.ItemsPanelRoot is not Panel panel || e.OriginalSource is not DependencyObject source)
            return;
        if (VisualTree.FindAncestor<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(source) != null || VisualTree.FindAncestor<TextBox>(source) != null)
            return;
        bool onItem = VisualTree.FindAncestor<ListViewItem>(source) != null;
        if (onItem)
        {
            // on a row: only its empty part in the Detailed view (the name, icon and texts start a drag)
            if (pane.EffectiveViewMode != Core.Panels.ViewMode.Detailed || source is TextBlock or Image or FontIcon)
                return;
        }
        _candidate = true;
        _pointer = e.Pointer;
        _lastPointer = e.GetCurrentPoint(_list).Position;
        _start = e.GetCurrentPoint(panel).Position;
        _toggle = KeyboardState.IsDown(KeyboardState.Control);
    }

    private void PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_candidate && !_active)
            return;
        PaneViewModel? pane = _pane();
        if (pane == null || _pointer == null || e.Pointer.PointerId != _pointer.PointerId)
            return;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            End();
            return;
        }
        _lastPointer = e.GetCurrentPoint(_list).Position;
        if (!_active)
        {
            if (_list.ItemsPanelRoot is not Panel panel)
                return;
            Windows.Foundation.Point now = e.GetCurrentPoint(panel).Position;
            if (!MarqueeRules.HasStarted(now.X - _start.X, now.Y - _start.Y))
                return;
            // start: take over the mouse (no item drag), remember or clear the marks
            _active = true;
            _candidate = false;
            _started();
            _list.CapturePointer(e.Pointer);
            _markedAtStart = new HashSet<int>();
            IReadOnlyList<FileItemViewModel> items = pane.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i].IsMarked)
                    _markedAtStart.Add(i);
            if (!_toggle)
            {
                foreach (int i in _markedAtStart)
                    pane.SetMark(i, false);
                _markedAtStart.Clear();
            }
            _rectangle.Visibility = Visibility.Visible;
            if (_scrollTimer == null)
            {
                _scrollTimer = _list.DispatcherQueue.CreateTimer();
                _scrollTimer.Interval = TimeSpan.FromMilliseconds(30);
                _scrollTimer.Tick += (_, _) => AutoScroll();
            }
            _scrollTimer.Start();
        }
        e.Handled = true;
        Update();
    }

    /// <summary>Draws the rectangle and marks the realized rows it touches.</summary>
    private void Update()
    {
        PaneViewModel? pane = _pane();
        if (!_active || pane == null || _list.ItemsPanelRoot is not Panel panel)
            return;
        GeneralTransform listToPanel = _list.TransformToVisual(panel);
        Windows.Foundation.Point current = listToPanel.TransformPoint(_lastPointer);
        double rectLeft = Math.Min(_start.X, current.X), rectTop = Math.Min(_start.Y, current.Y);
        double rectRight = Math.Max(_start.X, current.X), rectBottom = Math.Max(_start.Y, current.Y);

        // drawing: panel coordinates → overlay, clipped to the visible list
        GeneralTransform panelToLayer = panel.TransformToVisual(_layer);
        Windows.Foundation.Point a = panelToLayer.TransformPoint(new Windows.Foundation.Point(rectLeft, rectTop));
        Windows.Foundation.Point b = panelToLayer.TransformPoint(new Windows.Foundation.Point(rectRight, rectBottom));
        double left = Math.Clamp(Math.Min(a.X, b.X), 0, _layer.ActualWidth);
        double top = Math.Clamp(Math.Min(a.Y, b.Y), 0, _layer.ActualHeight);
        double right = Math.Clamp(Math.Max(a.X, b.X), 0, _layer.ActualWidth);
        double bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, _layer.ActualHeight);
        Canvas.SetLeft(_rectangle, left);
        Canvas.SetTop(_rectangle, top);
        _rectangle.Width = Math.Max(0, right - left);
        _rectangle.Height = Math.Max(0, bottom - top);

        // marking: realized rows only (rows scrolled away keep what they got while visible)
        foreach (UIElement child in panel.Children)
        {
            if (child is not ListViewItem container || container.Visibility != Visibility.Visible)
                continue;
            int index = _list.IndexFromContainer(container);
            if (index < 0)
                continue;
            Windows.Foundation.Point origin = container.TransformToVisual(panel).TransformPoint(new Windows.Foundation.Point(0, 0));
            bool inside = MarqueeRules.Overlaps(origin.X, origin.Y, origin.X + container.ActualWidth, origin.Y + container.ActualHeight,
                rectLeft, rectTop, rectRight, rectBottom);
            pane.SetMark(index, MarqueeRules.MarkFor(inside, _toggle, _markedAtStart.Contains(index)));
        }
        pane.NotifyMarksChanged();
    }

    /// <summary>Scrolls while the mouse is beyond the list's edges (vertically, sideways in Brief).</summary>
    private void AutoScroll()
    {
        ScrollViewer? scroller = _scroller();
        if (!_active || scroller == null)
            return;
        double dx = scroller.HorizontalScrollMode == ScrollMode.Disabled ? 0 : MarqueeRules.AutoScrollStep(_lastPointer.X, _list.ActualWidth);
        double dy = scroller.VerticalScrollMode == ScrollMode.Disabled ? 0 : MarqueeRules.AutoScrollStep(_lastPointer.Y, _list.ActualHeight);
        if (dx == 0 && dy == 0)
            return;
        scroller.ChangeView(scroller.HorizontalOffset + dx, scroller.VerticalOffset + dy, null, disableAnimation: true);
        // the content moved under the mouse: redraw after the new layout
        _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Update);
    }

    private void PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_active)
        {
            Update();
            e.Handled = true;
        }
        End();
    }

    private void CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // the row losing its capture to the list (when the rectangle starts) bubbles here too: only the list's own loss ends it
        if (_active && ReferenceEquals(e.OriginalSource, _list))
            End();
    }

    private void End()
    {
        bool wasActive = _active;
        _active = false;
        _candidate = false;
        _scrollTimer?.Stop();
        _rectangle.Visibility = Visibility.Collapsed;
        if (wasActive && _pointer != null)
            _list.ReleasePointerCapture(_pointer);
        _pointer = null;
    }
}
