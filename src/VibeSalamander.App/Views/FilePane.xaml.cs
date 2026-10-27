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

/// <summary>Chooses the row template: drives (This PC) or files.</summary>
public sealed partial class FileRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileTemplate { get; set; }
    public DataTemplate? DriveTemplate { get; set; }
    public DataTemplate? BriefTemplate { get; set; }
    public DataTemplate? IconTemplate { get; set; }
    public DataTemplate? ThumbnailTemplate { get; set; }
    public DataTemplate? TileTemplate { get; set; }
    public DataTemplate? DriveTileTemplate { get; set; }

    /// <summary>The panel's view; drives (This PC) use their own row template in the Detailed view.</summary>
    public ViewMode Mode { get; set; } = ViewMode.Detailed;

    /// <summary>A copy for another view (assigning a new selector makes the list re-template its items).</summary>
    public FileRowTemplateSelector WithMode(ViewMode mode) => new()
    {
        FileTemplate = FileTemplate,
        DriveTemplate = DriveTemplate,
        BriefTemplate = BriefTemplate,
        IconTemplate = IconTemplate,
        ThumbnailTemplate = ThumbnailTemplate,
        TileTemplate = TileTemplate,
        DriveTileTemplate = DriveTileTemplate,
        Mode = mode,
    };

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is FileItemViewModel { IsDrive: true } && Mode is ViewMode.Detailed or ViewMode.Tiles
            ? (Mode == ViewMode.Tiles ? DriveTileTemplate : DriveTemplate)
            : Mode switch
        {
            ViewMode.Brief => BriefTemplate,
            ViewMode.Icons => IconTemplate,
            ViewMode.Thumbnails => ThumbnailTemplate,
            ViewMode.Tiles => TileTemplate,
            _ => FileTemplate,
        };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>One panel: drive bar, directory line, header, file list, information line.</summary>
public sealed partial class FilePane : UserControl
{
    public FilePane()
    {
        InitializeComponent();
        // ListViewItems handle PointerPressed themselves; listen to handled events too for Ctrl+click marking.
        FileList.AddHandler(PointerPressedEvent, new PointerEventHandler(List_PointerPressed), handledEventsToo: true);
        FileList.Loaded += (_, _) => TrackedScroller(); // the scroll position is followed from the start
        // the list items handle Tapped themselves (selection): listen to handled taps too
        FileList.AddHandler(TappedEvent, new TappedEventHandler(List_Tapped), handledEventsToo: true);
        // any click inside the panel makes it the active one (also on the empty area below the items)
        RootGrid.AddHandler(PointerPressedEvent, new PointerEventHandler(Pane_PointerPressed), handledEventsToo: true);
        // mouse rectangle selection: moves and the release arrive handled when they start on an item
        FileList.AddHandler(PointerMovedEvent, new PointerEventHandler(Marquee_PointerMoved), handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, new PointerEventHandler(Marquee_PointerReleased), handledEventsToo: true);
        FileList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Marquee_CaptureLost), handledEventsToo: true);
    }

    public PaneViewModel? ViewModel { get; private set; }
    private MainViewModel? _main;

    public void Initialize(PaneViewModel viewModel, MainViewModel main)
    {
        ViewModel = viewModel;
        _main = main;
        viewModel.ListingChanged += (_, e) =>
        {
            if (e.SamePlace)
                KeepScrollPosition(e.CaretIndex, focus: viewModel.IsActive);
            else
                ApplyCaret(e.CaretIndex, focus: viewModel.IsActive);
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PaneViewModel.DisplayPath) or nameof(PaneViewModel.Path) or nameof(PaneViewModel.Drives) or nameof(PaneViewModel.VisibleDrives))
                RebuildBreadcrumb();
            else if (e.PropertyName == nameof(PaneViewModel.IsActive))
                UpdateBreadcrumbForeground(); // not a rebuild: a chevron click activates the panel, and its menu needs the button
            if (e.PropertyName is nameof(PaneViewModel.EffectiveViewMode))
                ApplyViewMode();
        };
        viewModel.ListingChanged += (_, _) =>
        {
            if (viewModel.EffectiveViewMode == ViewMode.Brief)
                ApplyViewMode(); // column width follows the longest name
        };
        ApplyViewMode();
        Bindings.Update();
        RebuildBreadcrumb();
    }

    /// <summary>Re-evaluates the x:Bind bindings (after theme changes).</summary>
    public void RefreshBindings()
    {
        Bindings.Update();
        UpdateBreadcrumbForeground();
    }

    public void SetBarsVisibility(bool driveBar, bool infoLine)
    {
        DriveBar.Visibility = driveBar ? Visibility.Visible : Visibility.Collapsed;
        InfoLine.Visibility = infoLine ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- x:Bind helpers --------------------------------------------------------------------

    public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisibleIfNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Directory line text: on-accent color when active, normal text otherwise.</summary>
    public Brush PathForeground(bool active)
    {
        if (!active)
            return UiState.TextBrush;
        // accent fill is dark in light theme and light in dark theme
        return new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
    }

    public string SortGlyph(SortMode mode, bool descending, string column) =>
        mode.ToString() == column ? (descending ^ (mode == SortMode.Size) ? "\uE70D" : "\uE70E") : string.Empty; // size: largest first points down
}
