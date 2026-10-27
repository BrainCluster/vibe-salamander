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

// FilePane: the caret (the focused item), keyboard focus and keeping the scroll position.
public sealed partial class FilePane
{
    // ---- caret & focus ---------------------------------------------------------------------

    private void ApplyCaret(int index, bool focus)
    {
        if (index >= 0 && index < FileList.Items.Count)
        {
            FileList.SelectedIndex = index;
            FileList.ScrollIntoView(FileList.Items[index]);
        }
        if (focus && !IsRenaming)
            FocusList();
    }

    // where the list was scrolled to (kept up to date by the ScrollViewer)
    private double _scrollX, _scrollY;
    private bool _scrollTracked;
    private ScrollViewer? _listScroller;

    private ScrollViewer? TrackedScroller()
    {
        _listScroller ??= FindDescendant<ScrollViewer>(FileList, _ => true);
        if (_listScroller != null && !_scrollTracked)
        {
            _scrollTracked = true;
            _scrollX = _listScroller.HorizontalOffset;
            _scrollY = _listScroller.VerticalOffset;
            _listScroller.ViewChanged += (_, _) =>
            {
                _scrollX = _listScroller.HorizontalOffset;
                _scrollY = _listScroller.VerticalOffset;
            };
        }
        return _listScroller;
    }

    /// <summary>
    /// The same directory listed again (a refresh after deleting, copying, a new sort): the rows stay where
    /// they were on the screen instead of the caret jumping to the bottom edge. Only when the caret would
    /// be out of sight is it scrolled into view.
    /// </summary>
    private void KeepScrollPosition(int index, bool focus)
    {
        ScrollViewer? scroller = TrackedScroller();
        if (scroller == null || index < 0 || index >= FileList.Items.Count)
        {
            ApplyCaret(index, focus);
            return;
        }
        double x = _scrollX, y = _scrollY; // before the new rows reset the scroll position
        FileList.SelectedIndex = index;
        FileList.UpdateLayout();
        scroller.ChangeView(x, y, null, disableAnimation: true);
        FileList.UpdateLayout();
        if (!IsShown(scroller, index))
            FileList.ScrollIntoView(FileList.Items[index]);
        if (focus && !IsRenaming)
            FocusList();
    }

    /// <summary>True when the row is completely inside the visible part of the list.</summary>
    private bool IsShown(ScrollViewer scroller, int index)
    {
        if (FileList.ContainerFromIndex(index) is not FrameworkElement row || row.ActualHeight <= 0)
            return false;
        Windows.Foundation.Point p = row.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0));
        bool vertically = p.Y >= 0 && p.Y + row.ActualHeight <= scroller.ViewportHeight + 0.5;
        // sideways only matters where rows are narrower than the list (the Brief view's columns)
        bool sideways = row.ActualWidth >= scroller.ViewportWidth || (p.X >= 0 && p.X + row.ActualWidth <= scroller.ViewportWidth + 0.5);
        return vertically && sideways;
    }

    public void MoveCaret(int index)
    {
        ApplyCaret(index, focus: true);
    }

    /// <summary>Moves keyboard focus into the list, on the caret item.</summary>
    public void FocusList()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // the address bar is being edited (Shift+F7 from the F-key bar, an auto refresh while typing)
            if (PathEditor.Visibility == Visibility.Visible && XamlRoot != null && FocusManager.GetFocusedElement(XamlRoot) == PathEditor)
                return;
            // a menu, flyout or dialog is open (user menu, lists, the user menu editor): don't take the focus away from it
            if (XamlRoot != null && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => p.Child is MenuFlyoutPresenter or FlyoutPresenter or ContentDialog))
                return;
            int index = FileList.SelectedIndex;
            if (index >= 0 && FileList.ContainerFromIndex(index) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            else
                FileList.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Where a Shift+click range starts: the caret position last reached without Shift held.</summary>
    private int _shiftAnchor = -1;

    /// <summary>The caret before the last selection change (a click on the focused item doesn't change it).</summary>
    private int _previousCaret = -1;
    private DateTime _caretChangedAt;

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (object removed in e.RemovedItems)
            if (removed is FileItemViewModel r)
                r.IsCaret = false;
        foreach (object added in e.AddedItems)
            if (added is FileItemViewModel a)
                a.IsCaret = true;
        if (ViewModel != null && FileList.SelectedIndex >= 0)
        {
            if (!KeyboardState.IsDown(KeyboardState.Shift))
                _shiftAnchor = FileList.SelectedIndex;
            _previousCaret = ViewModel.CaretIndex;
            _caretChangedAt = DateTime.UtcNow;
            ViewModel.CaretIndex = FileList.SelectedIndex;
            if (e.AddedItems.Count > 0 && ViewModel.QuickSearch.Length > 0 && !_quickSearchMoving)
                ViewModel.EndQuickSearch();
        }
    }

    private void List_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            _main?.Activate(ViewModel);
    }

    private async void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _slowClickTimer?.Stop();
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item || item.IsRenaming)
            return; // double-clicking inside the rename box selects a word
        if (_main.Settings.Appearance.SingleClickOpens)
            return; // the first click already opened it
        int index = IndexOf(item);
        if (index < 0)
            return;
        _main.Activate(ViewModel);
        ViewModel.CaretIndex = index;
        await _main.ExecuteAsync(CommandId.Open);
    }
}
