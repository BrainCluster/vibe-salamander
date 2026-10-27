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

// FilePane: the views (Alt+1…5), their layouts and pictures, and the Detailed view columns.
public sealed partial class FilePane
{
    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.Item is FileItemViewModel recycled)
                recycled.ReleaseLargeIcon(); // off screen: thumbnails are the big part of a row's memory
            return;
        }
        if (args.Item is not FileItemViewModel item || _main == null)
            return;
        if (args.Phase == 0)
        {
            // the row's texts first; its icon in a later pass, which the list skips for rows that are
            // scrolled past quickly (dragging the scrollbar through thousands of files stays smooth)
            args.RegisterUpdateCallback(List_ContainerContentChanging);
            return;
        }
        if (ViewModel?.CurrentDriveOptions?.SimpleIcons == true)
            return; // Configuration → Drives: generic icons on this kind of drive
        item.EnsureIcon(_main.Icons);
        double dips = _appliedView switch
        {
            ViewMode.Icons => 48,
            ViewMode.Tiles => 48,
            ViewMode.Thumbnails => UiState.ThumbnailSize,
            _ => 0,
        };
        if (dips > 0 && !ViewModel!.IsRemote) // a server's files would have to be downloaded for their pictures
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            item.EnsureLargeIcon(_main.Thumbnails, (uint)Math.Ceiling(dips * scale));
        }
    }

    // ---- views (Alt+1…5) -------------------------------------------------------------------

    private ViewMode _appliedView = (ViewMode)(-1);
    private double _appliedBriefWidth;
    private double _appliedRowHeight;
    private int _appliedThumbnailSize;

    private static double ThumbnailItemWidth => Math.Max(UiState.ThumbnailSize + 28, 76); // room for a name under small icons
    private static double ThumbnailItemHeight => UiState.ThumbnailSize + 40;

    /// <summary>The thumbnail size slider moved: the Thumbnails view follows at once.</summary>
    public void ApplyThumbnailSize() => ApplyViewMode();

    private void ResizeThumbnails(ItemsWrapGrid grid)
    {
        grid.ItemWidth = ThumbnailItemWidth;
        grid.ItemHeight = ThumbnailItemHeight;
        foreach (UIElement child in grid.Children)
        {
            if (child is ListViewItem { Content: FileItemViewModel item })
                item.RefreshThumbnailSize();
        }
    }

    /// <summary>After the slider stops: the visible pictures load again when they're now larger than before.</summary>
    public void ReloadThumbnails()
    {
        if (_appliedView != ViewMode.Thumbnails || _main == null || ViewModel is not { IsRemote: false } ||
            FileList.ItemsPanelRoot is not ItemsWrapGrid grid)
            return;
        uint size = (uint)Math.Ceiling(UiState.ThumbnailSize * (XamlRoot?.RasterizationScale ?? 1));
        foreach (UIElement child in grid.Children)
        {
            if (child is ListViewItem { Content: FileItemViewModel item })
                item.EnsureLargeIcon(_main.Thumbnails, size);
        }
    }

    /// <summary>
    /// The Thumbnails view was left: its pictures are let go at once (rows scrolled past keep theirs until they are
    /// recycled, and the kept ones fill the cache), and the memory goes back to Windows. Icons and Tiles load their
    /// smaller pictures again.
    /// </summary>
    private void ReleaseThumbnails()
    {
        if (ViewModel == null || _main == null)
            return;
        foreach (FileItemViewModel item in ViewModel.Items)
            item.ReleaseLargeIcon();
        PaneViewModel other = ReferenceEquals(ViewModel, _main.Left) ? _main.Right : _main.Left;
        if (other.EffectiveViewMode != ViewMode.Thumbnails)
            _main.Thumbnails.Clear(); // the other panel doesn't show thumbnails either: nothing to keep them for
        // a moment later: the list lets go of the thumbnail rows when it re-templates them for the new view
        MemoryCleanup.Schedule();
    }

    private static ItemsPanelTemplate PanelTemplate(string panel) =>
        (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" + panel + "</ItemsPanelTemplate>");

    private static string Num(double value) => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Brief view column width: enough for the longest name, within limits (like Salamander).</summary>
    private double BriefColumnWidth()
    {
        int longest = 8;
        if (ViewModel != null)
            foreach (FileItemViewModel item in ViewModel.Items)
                longest = Math.Max(longest, item.FullName.Length);
        double charWidth = 6.6 * UiState.FontSize / 12;
        return Math.Round(Math.Clamp(longest * charWidth + 46, 126, 386)); // + icon and the row padding
    }

    /// <summary>Detailed view: every row as wide as the list.</summary>
    private double DetailedRowWidth() => FileList.ActualWidth > 0 ? Math.Floor(FileList.ActualWidth) : 800;

    /// <summary>The list was resized: the Detailed rows follow its width.</summary>
    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_appliedView == ViewMode.Detailed && FileList.ItemsPanelRoot is ItemsWrapGrid wrap)
            wrap.ItemWidth = DetailedRowWidth();
    }

    /// <summary>Switches the list's layout and item templates to the panel's view.</summary>
    private void ApplyViewMode()
    {
        if (ViewModel == null)
            return;
        PaneViewModel vm = ViewModel;
        ViewMode mode = vm.EffectiveViewMode;
        double briefWidth = mode == ViewMode.Brief ? BriefColumnWidth() : 0;
        double rowHeight = UiState.RowHeight; // density changes the Brief item height
        int thumbnailSize = UiState.ThumbnailSize;
        if (mode == _appliedView && briefWidth == _appliedBriefWidth && rowHeight == _appliedRowHeight &&
            (mode != ViewMode.Thumbnails || thumbnailSize == _appliedThumbnailSize))
            return;
        bool modeChanged = mode != _appliedView;
        ViewMode previous = _appliedView;
        _appliedView = mode;
        if (modeChanged && previous == ViewMode.Thumbnails)
            ReleaseThumbnails();
        _appliedBriefWidth = briefWidth;
        _appliedRowHeight = rowHeight;
        _appliedThumbnailSize = thumbnailSize;

        // Thumbnails with a new size (the slider): resize the existing panel and the visible items
        if (!modeChanged && mode == ViewMode.Thumbnails && FileList.ItemsPanelRoot is ItemsWrapGrid grid)
        {
            ResizeThumbnails(grid);
            return;
        }

        // Brief after a reload: resize the existing panel instead of swapping it (no re-realization, no flicker)
        if (!modeChanged && mode == ViewMode.Brief && FileList.ItemsPanelRoot is ItemsWrapGrid wrap)
        {
            wrap.ItemWidth = briefWidth;
            wrap.ItemHeight = rowHeight;
            return;
        }

        FileList.ItemsPanel = mode switch
        {
            ViewMode.Brief => PanelTemplate($"<ItemsWrapGrid Orientation='Vertical' ItemWidth='{Num(briefWidth)}' ItemHeight='{Num(rowHeight)}' CacheLength='1' />"),
            ViewMode.Icons => PanelTemplate("<ItemsWrapGrid Orientation='Horizontal' ItemWidth='96' ItemHeight='104' CacheLength='1' />"),
            ViewMode.Thumbnails => PanelTemplate($"<ItemsWrapGrid Orientation='Horizontal' ItemWidth='{Num(ThumbnailItemWidth)}' ItemHeight='{Num(ThumbnailItemHeight)}' CacheLength='1' />"),
            ViewMode.Tiles => PanelTemplate("<ItemsWrapGrid Orientation='Horizontal' ItemWidth='264' ItemHeight='64' CacheLength='1' />"),
            // one item per line with a fixed height: the list knows its exact length (a steady scrollbar with
            // thousands of files) and builds few rows ahead (CacheLength), which keeps memory down
            _ => PanelTemplate($"<ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='1' ItemHeight='{Num(rowHeight)}' ItemWidth='{Num(DetailedRowWidth())}' CacheLength='1' />"),
        };
        bool sideways = mode == ViewMode.Brief;
        ScrollViewer.SetHorizontalScrollMode(FileList, sideways ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(FileList, sideways ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(FileList, sideways ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(FileList, sideways ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        if (modeChanged && Resources["RowTemplateSelector"] is FileRowTemplateSelector selector)
            FileList.ItemTemplateSelector = selector.WithMode(mode);

        // keep the caret visible in the new layout; read it when this runs: a view change raised by navigation
        // (This PC <-> a folder) comes before the new listing sets its caret, and the old index would win
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ApplyCaret(vm.CaretIndex, focus: vm.IsActive));
    }

    public Visibility HeaderVisibility(ViewMode mode, bool thisPc) =>
        mode == ViewMode.Detailed && !thisPc ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The drive list's header: This PC in the Detailed view.</summary>
    public Visibility ThisPcHeaderVisibility(ViewMode mode, bool thisPc) =>
        mode == ViewMode.Detailed && thisPc ? Visibility.Visible : Visibility.Collapsed;

    // ---- Detailed view columns: resize (header grips) and choose (header right-click) -----------

    private DetailColumn? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private void ColumnGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || sender is not UIElement grip || (sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse(tag, out DetailColumn column))
            return;
        _resizingColumn = column;
        _resizeStartX = e.GetCurrentPoint(FileHeader).Position.X;
        _resizeStartWidth = ViewModel.Columns.GetWidth(column);
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ColumnGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingColumn is not DetailColumn column || ViewModel == null)
            return;
        if (!e.Pointer.IsInContact)
        {
            _resizingColumn = null; // capture lost without a release (Alt+Tab, a popup): hovering must not resize
            return;
        }
        // the grip is on the column's left edge: dragging left makes the column wider
        double dx = e.GetCurrentPoint(FileHeader).Position.X - _resizeStartX;
        ViewModel.Columns.SetWidth(column, _resizeStartWidth - dx);
        e.Handled = true;
    }

    private void ColumnGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingColumn = null;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void ColumnGrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel != null && (sender as FrameworkElement)?.Tag is string tag && Enum.TryParse(tag, out DetailColumn column))
            ViewModel.Columns.SetWidth(column, ColumnLayout.DefaultWidth(column));
        e.Handled = true;
    }

    private void Header_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel == null)
            return;
        ColumnLayout columns = ViewModel.Columns;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Name", IsEnabled = false });
        foreach (DetailColumn column in Enum.GetValues<DetailColumn>())
        {
            string label = column switch
            {
                DetailColumn.Ext => "Extension",
                DetailColumn.Attr => "Attributes",
                _ => column.ToString(),
            };
            var toggle = new ToggleMenuFlyoutItem { Text = label, IsChecked = columns.IsVisible(column) };
            DetailColumn c = column;
            toggle.Click += (_, _) => columns.SetVisible(c, toggle.IsChecked);
            menu.Items.Add(toggle);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Reset Columns" };
        reset.Click += (_, _) => columns.Reset();
        menu.Items.Add(reset);
        menu.Closed += (_, _) => FocusList();
        Loc.Translate(menu);
        menu.ShowAt(FileHeader, e.GetPosition(FileHeader));
        e.Handled = true;
    }
}
