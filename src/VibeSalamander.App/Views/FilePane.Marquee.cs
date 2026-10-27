// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

// FilePane: selecting with a mouse rectangle.
public sealed partial class FilePane
{
    // ---- mouse rectangle selection -----------------------------------------------------------

    private bool _marqueeCandidate, _marqueeActive;
    private Windows.Foundation.Point _marqueeStart;        // in ItemsPanelRoot coordinates (moves with the content)
    private Windows.Foundation.Point _marqueeLastPointer;  // in FileList coordinates
    private Microsoft.UI.Xaml.Input.Pointer? _marqueePointer;
    private bool _marqueeToggle;
    private HashSet<int> _marqueeInitial = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _marqueeScrollTimer;
    private ScrollViewer? _listScroller;

    /// <summary>
    /// A left press on the empty area of the list (or, in the Detailed view, on the empty part of a row
    /// beside the name) can start a selection rectangle, like Explorer. Ctrl toggles marks instead of
    /// replacing them.
    /// </summary>
    private void MarqueePressed(PointerRoutedEventArgs e)
    {
        _marqueeCandidate = false;
        if (ViewModel == null || ViewModel.IsThisPc || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            FileList.ItemsPanelRoot is not Panel panel || e.OriginalSource is not DependencyObject source)
            return;
        if (FindAncestor<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(source) != null || FindAncestor<TextBox>(source) != null)
            return;
        bool onItem = FindAncestor<ListViewItem>(source) != null;
        if (onItem)
        {
            // on a row: only its empty part in the Detailed view (the name, icon and texts start a drag)
            if (ViewModel.EffectiveViewMode != ViewMode.Detailed || source is TextBlock or Image or FontIcon)
                return;
        }
        _marqueeCandidate = true;
        _marqueePointer = e.Pointer;
        _marqueeLastPointer = e.GetCurrentPoint(FileList).Position;
        _marqueeStart = e.GetCurrentPoint(panel).Position;
        _marqueeToggle = KeyboardState.IsDown(KeyboardState.Control);
    }

    private void Marquee_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeCandidate && !_marqueeActive)
            return;
        if (ViewModel == null || _marqueePointer == null || e.Pointer.PointerId != _marqueePointer.PointerId)
            return;
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            EndMarquee();
            return;
        }
        _marqueeLastPointer = e.GetCurrentPoint(FileList).Position;
        if (!_marqueeActive)
        {
            if (FileList.ItemsPanelRoot is not Panel panel)
                return;
            Windows.Foundation.Point now = e.GetCurrentPoint(panel).Position;
            if (Math.Abs(now.X - _marqueeStart.X) < 4 && Math.Abs(now.Y - _marqueeStart.Y) < 4)
                return;
            // start: take over the mouse (no item drag), remember or clear the marks
            _marqueeActive = true;
            _marqueeCandidate = false;
            _slowClickTimer?.Stop();
            FileList.CapturePointer(e.Pointer);
            _marqueeInitial = new HashSet<int>();
            IReadOnlyList<FileItemViewModel> items = ViewModel.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i].IsMarked)
                    _marqueeInitial.Add(i);
            if (!_marqueeToggle)
            {
                foreach (int i in _marqueeInitial)
                    ViewModel.SetMark(i, false);
                _marqueeInitial.Clear();
            }
            Marquee.Visibility = Visibility.Visible;
            _listScroller ??= FindDescendant<ScrollViewer>(FileList, _ => true);
            if (_marqueeScrollTimer == null)
            {
                _marqueeScrollTimer = DispatcherQueue.CreateTimer();
                _marqueeScrollTimer.Interval = TimeSpan.FromMilliseconds(30);
                _marqueeScrollTimer.Tick += (_, _) => MarqueeAutoScroll();
            }
            _marqueeScrollTimer.Start();
        }
        e.Handled = true;
        UpdateMarquee();
    }

    /// <summary>Draws the rectangle and marks the realized items it touches.</summary>
    private void UpdateMarquee()
    {
        if (!_marqueeActive || ViewModel == null || FileList.ItemsPanelRoot is not Panel panel)
            return;
        GeneralTransform listToPanel = FileList.TransformToVisual(panel);
        Windows.Foundation.Point current = listToPanel.TransformPoint(_marqueeLastPointer);
        var rect = new Windows.Foundation.Rect(
            Math.Min(_marqueeStart.X, current.X), Math.Min(_marqueeStart.Y, current.Y),
            Math.Abs(current.X - _marqueeStart.X), Math.Abs(current.Y - _marqueeStart.Y));

        // drawing: panel coordinates → overlay, clipped to the visible list
        GeneralTransform panelToLayer = panel.TransformToVisual(MarqueeLayer);
        Windows.Foundation.Point a = panelToLayer.TransformPoint(new Windows.Foundation.Point(rect.Left, rect.Top));
        Windows.Foundation.Point b = panelToLayer.TransformPoint(new Windows.Foundation.Point(rect.Right, rect.Bottom));
        double left = Math.Clamp(Math.Min(a.X, b.X), 0, MarqueeLayer.ActualWidth);
        double top = Math.Clamp(Math.Min(a.Y, b.Y), 0, MarqueeLayer.ActualHeight);
        double right = Math.Clamp(Math.Max(a.X, b.X), 0, MarqueeLayer.ActualWidth);
        double bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, MarqueeLayer.ActualHeight);
        Canvas.SetLeft(Marquee, left);
        Canvas.SetTop(Marquee, top);
        Marquee.Width = Math.Max(0, right - left);
        Marquee.Height = Math.Max(0, bottom - top);

        // marking: realized rows only (rows scrolled away keep what they got while visible)
        foreach (UIElement child in panel.Children)
        {
            if (child is not ListViewItem container || container.Visibility != Visibility.Visible)
                continue;
            int index = FileList.IndexFromContainer(container);
            if (index < 0)
                continue;
            Windows.Foundation.Point origin = container.TransformToVisual(panel).TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
            bool inside = bounds.Left < rect.Right && bounds.Right > rect.Left && bounds.Top < rect.Bottom && bounds.Bottom > rect.Top;
            bool initial = _marqueeInitial.Contains(index);
            ViewModel.SetMark(index, inside ? (_marqueeToggle ? !initial : true) : initial);
        }
        ViewModel.NotifyMarksChanged();
    }

    /// <summary>Scrolls while the mouse is beyond the list's edges (vertically, sideways in Brief).</summary>
    private void MarqueeAutoScroll()
    {
        if (!_marqueeActive || _listScroller == null)
            return;
        double dx = 0, dy = 0;
        Windows.Foundation.Point p = _marqueeLastPointer;
        double edge = 12;
        if (p.Y < edge) dy = -Math.Min(40, edge - p.Y + 4);
        else if (p.Y > FileList.ActualHeight - edge) dy = Math.Min(40, p.Y - (FileList.ActualHeight - edge) + 4);
        if (p.X < edge) dx = -Math.Min(40, edge - p.X + 4);
        else if (p.X > FileList.ActualWidth - edge) dx = Math.Min(40, p.X - (FileList.ActualWidth - edge) + 4);
        if (_listScroller.HorizontalScrollMode == ScrollMode.Disabled) dx = 0;
        if (_listScroller.VerticalScrollMode == ScrollMode.Disabled) dy = 0;
        if (dx == 0 && dy == 0)
            return;
        _listScroller.ChangeView(_listScroller.HorizontalOffset + dx, _listScroller.VerticalOffset + dy, null, disableAnimation: true);
        // the content moved under the mouse: redraw after the new layout
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateMarquee);
    }

    private void Marquee_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeActive)
        {
            UpdateMarquee();
            e.Handled = true;
        }
        EndMarquee();
    }

    private void Marquee_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // the row losing its capture to the list (when the rectangle starts) bubbles here too: only the list's own loss ends it
        if (_marqueeActive && ReferenceEquals(e.OriginalSource, FileList))
            EndMarquee();
    }

    private void EndMarquee()
    {
        bool wasActive = _marqueeActive;
        _marqueeActive = false;
        _marqueeCandidate = false;
        _marqueeScrollTimer?.Stop();
        Marquee.Visibility = Visibility.Collapsed;
        if (wasActive && _marqueePointer != null)
            FileList.ReleasePointerCapture(_marqueePointer);
        _marqueePointer = null;
    }
}
