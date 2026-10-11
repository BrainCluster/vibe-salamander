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

    /// <summary>The panel's view; drives (This PC) always use their own row template.</summary>
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
        Mode = mode,
    };

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is FileItemViewModel { IsDrive: true } ? DriveTemplate : Mode switch
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
        viewModel.ListingChanged += (_, e) => ApplyCaret(e.CaretIndex, focus: viewModel.IsActive);
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

    // ---- mouse options (Configuration → Panels / Appearance) -------------------------------

    private int _caretAtPress = -1;
    private DateTime _pressTime;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slowClickTimer;

    /// <summary>Single click opens (Appearance) or a slow second click on the focused item renames (Panels).</summary>
    private async void List_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null || IsRenaming || e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            return;
        if (KeyboardState.CurrentModifiers() != KeyModifiers.None)
            return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item)
            return;
        int index = IndexOf(item);
        if (index < 0)
            return;
        if (_main.Settings.Appearance.SingleClickOpens)
        {
            ViewModel.CaretIndex = index;
            await _main.ExecuteAsync(CommandId.Open);
            return;
        }
        if (!_main.Settings.Panels.SlowClickRename || index != _caretAtPress || item.Entry.IsUpDir || item.IsDrive)
            return;
        // like Explorer: only a click on the name itself renames (not the icon, size, date or empty space)
        if (e.OriginalSource is not TextBlock { Tag: "NameText" })
            return;
        // like Explorer: wait out the double-click time; a double-click cancels the rename
        if (_slowClickTimer == null)
        {
            _slowClickTimer = DispatcherQueue.CreateTimer();
            _slowClickTimer.IsRepeating = false;
            _slowClickTimer.Tick += async (_, _) =>
            {
                if (ViewModel != null && _main != null && !IsRenaming && ViewModel.CaretIndex == _slowClickIndex)
                    await _main.ExecuteAsync(CommandId.QuickRename);
            };
        }
        _slowClickIndex = index;
        _slowClickTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, GetDoubleClickTime()) + 50);
        _slowClickTimer.Stop();
        _slowClickTimer.Start();
    }

    private int _slowClickIndex = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItemViewModel item || _main == null)
            return;
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
        if (dips > 0)
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            item.EnsureLargeIcon(_main.Thumbnails, (uint)Math.Ceiling(dips * scale));
        }
    }

    // ---- views (Alt+1…5) -------------------------------------------------------------------

    private ViewMode _appliedView = (ViewMode)(-1);
    private double _appliedBriefWidth;
    private double _appliedRowHeight;

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

    /// <summary>Switches the list's layout and item templates to the panel's view.</summary>
    private void ApplyViewMode()
    {
        if (ViewModel == null)
            return;
        PaneViewModel vm = ViewModel;
        ViewMode mode = vm.EffectiveViewMode;
        double briefWidth = mode == ViewMode.Brief ? BriefColumnWidth() : 0;
        double rowHeight = UiState.RowHeight; // density changes the Brief item height
        if (mode == _appliedView && briefWidth == _appliedBriefWidth && rowHeight == _appliedRowHeight)
            return;
        bool modeChanged = mode != _appliedView;
        _appliedView = mode;
        _appliedBriefWidth = briefWidth;
        _appliedRowHeight = rowHeight;

        // Brief after a reload: resize the existing panel instead of swapping it (no re-realization, no flicker)
        if (!modeChanged && mode == ViewMode.Brief && FileList.ItemsPanelRoot is ItemsWrapGrid wrap)
        {
            wrap.ItemWidth = briefWidth;
            wrap.ItemHeight = rowHeight;
            return;
        }

        FileList.ItemsPanel = mode switch
        {
            ViewMode.Brief => PanelTemplate($"<ItemsWrapGrid Orientation='Vertical' ItemWidth='{Num(briefWidth)}' ItemHeight='{Num(rowHeight)}' />"),
            ViewMode.Icons => PanelTemplate("<ItemsWrapGrid Orientation='Horizontal' ItemWidth='96' ItemHeight='104' />"),
            ViewMode.Thumbnails => PanelTemplate($"<ItemsWrapGrid Orientation='Horizontal' ItemWidth='{Num(UiState.ThumbnailSize + 28)}' ItemHeight='{Num(UiState.ThumbnailSize + 40)}' />"),
            ViewMode.Tiles => PanelTemplate("<ItemsWrapGrid Orientation='Horizontal' ItemWidth='264' ItemHeight='64' />"),
            _ => PanelTemplate("<ItemsStackPanel />"),
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

    // ---- drive information (Ctrl+F1) ---------------------------------------------------------

    private static string DriveTypeName(DriveType type) => type switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Removable => "Removable disk",
        DriveType.CDRom => "CD/DVD drive",
        DriveType.Network => "Network drive",
        DriveType.Ram => "RAM disk",
        _ => "Drive",
    };

    /// <summary>Volume details of the panel's drive (or the focused drive in This PC) in a flyout at the address bar.</summary>
    public async void ShowDriveInformation()
    {
        if (ViewModel == null || _main == null)
            return;
        string? path = ViewModel.IsThisPc ? ViewModel.FocusedItem?.Drive?.Root : ViewModel.IsLocalDisk ? ViewModel.Path : null;
        if (path == null)
        {
            await _main.ShowErrorAsync("Focus a drive (or open a folder on it) to see its information.");
            return;
        }
        DriveInformation? info = await DriveInformation.QueryAsync(path);
        if (info == null)
        {
            await _main.ShowErrorAsync($"No information is available for {DriveInformation.RootOf(path) ?? path} (is the drive ready?).");
            return;
        }

        string title = info.Root.Length <= 3
            ? $"{(info.Label.Length > 0 ? info.Label : DriveTypeName(info.Type))} ({info.Root.TrimEnd('\\')})"
            : info.Root.TrimEnd('\\');
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = title, Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style });
        if (info.TotalBytes > 0)
        {
            panel.Children.Add(new ProgressBar { Maximum = 100, Value = info.UsedPercent, Height = 6, MinHeight = 6, CornerRadius = new CornerRadius(3) });
        }
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, string value)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Opacity = 0.7 };
            var v = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }
        string Size(long bytes) => $"{Core.Formatting.Formats.ShortSize(bytes)}  ({Core.Formatting.Formats.ExactSize(bytes)} bytes)";
        Row("Type", DriveTypeName(info.Type));
        Row("File system", info.FileSystem.Length > 0 ? info.FileSystem : "-");
        if (info.SerialNumber != 0)
            Row("Serial number", info.SerialText);
        if (info.TotalBytes > 0)
        {
            Row("Capacity", Size(info.TotalBytes));
            Row("Used", $"{Size(info.TotalBytes - info.FreeBytes)},  {info.UsedPercent} %");
            Row("Free", Size(info.FreeBytes));
            if (info.AvailableBytes != info.FreeBytes)
                Row("Free for you", Size(info.AvailableBytes));
        }
        if (info.BytesPerCluster > 0)
            Row("Cluster size", Core.Formatting.Formats.ExactSize(info.BytesPerCluster) + " bytes");
        if (info.MaxComponentLength > 0)
            Row("Max name length", info.MaxComponentLength + " characters");
        Row("Supports", info.Features);
        panel.Children.Add(grid);

        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Closed += (_, _) => FocusList();
        Loc.Translate(panel);
        flyout.ShowAt(DriveMenuButton);
    }

    private void List_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || IsRenaming || !e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
            return;
        _pressTime = DateTime.UtcNow;
        MarqueePressed(e);
        bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
        bool shift = KeyboardState.IsDown(KeyboardState.Shift);
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item)
            return;
        int index = IndexOf(item);
        if (index < 0)
            return;
        // was the clicked item focused before this click? (the list may already have moved the caret)
        bool movedByThisClick = (DateTime.UtcNow - _caretChangedAt).TotalMilliseconds < 200 && ViewModel.CaretIndex == index;
        _caretAtPress = movedByThisClick ? _previousCaret : ViewModel.CaretIndex;
        if (ctrl && !shift)
        {
            ViewModel.ToggleMark(index); // Ctrl+click: mark / unmark one item
        }
        else if (shift && !ctrl)
        {
            // Shift+click: mark everything from the caret (before the click) to the clicked item
            int anchor = _shiftAnchor >= 0 ? _shiftAnchor : ViewModel.CaretIndex;
            if (anchor >= 0)
                ViewModel.MarkRange(anchor, index, mark: true);
        }
    }

    /// <summary>A click anywhere in the panel activates it; a click in the list area also gives the list the keyboard.</summary>
    private void Pane_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        // the empty area below the items isn't focusable by itself: put the focus on the caret item
        if (e.OriginalSource is DependencyObject source && IsInside(source, FileList) && !IsRenaming &&
            FindAncestor<TextBox>(source) == null)
            FocusList();
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (DependencyObject? d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is T t)
                return t;
        return null;
    }

    /// <summary>
    /// Shift+Up/Down/PgUp/PgDn/Home/End mark while moving, like Salamander: each item the caret leaves is
    /// marked (or unmarked when it was marked, so going back undoes it).
    /// </summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel == null || IsRenaming || KeyboardState.CurrentModifiers() != KeyModifiers.Shift)
            return;
        ViewMode view = ViewModel.EffectiveViewMode;
        if (view is not (ViewMode.Detailed or ViewMode.Brief))
            return; // in the grid views Up/Down jump rows; Ctrl+click / Shift+click mark there
        int count = ViewModel.Items.Count;
        int caret = ViewModel.CaretIndex;
        if (count == 0 || caret < 0)
            return;
        int page = Math.Max(1, (int)(FileList.ActualHeight / Math.Max(1, UiState.RowHeight)) - 1);
        int target = e.Key switch
        {
            VirtualKey.Up => caret - 1,
            VirtualKey.Down => caret + 1,
            VirtualKey.PageUp => caret - page,
            VirtualKey.PageDown => caret + page,
            VirtualKey.Home => 0,
            VirtualKey.End => count - 1,
            _ => int.MinValue,
        };
        if (target == int.MinValue)
            return;
        e.Handled = true;
        target = Math.Clamp(target, 0, count - 1);
        bool mark = !ViewModel.Items[caret].IsMarked;
        bool jump = e.Key is VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End;
        if (target == caret)
            ViewModel.MarkRange(caret, caret, mark); // at the first/last item: mark it
        else if (jump && (target == 0 || target == count - 1))
            ViewModel.MarkRange(caret, target, mark); // to the very start/end: including the last one
        else
            ViewModel.MarkRange(caret, target > caret ? target - 1 : target + 1, mark);
        ApplyCaret(target, focus: true);
    }

    private int IndexOf(FileItemViewModel item)
    {
        var items = ViewModel?.Items;
        if (items == null)
            return -1;
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item))
                return i;
        return -1;
    }

    // ---- quick search ----------------------------------------------------------------------

    private bool _quickSearchMoving;

    private void List_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (ViewModel == null || IsRenaming)
            return; // typing goes to the rename box
        char c = args.Character;
        if (char.IsControl(c))
            return;
        if (c == ' ' && ViewModel.QuickSearch.Length == 0)
            return; // Space marks the item
        bool ctrl = Services.KeyboardState.IsDown(Services.KeyboardState.Control);
        bool alt = Services.KeyboardState.IsDown(Services.KeyboardState.Alt);
        bool toCommandLine = _main?.Settings.Panels.TypingToCommandLine == true;
        if (ctrl || (alt && !toCommandLine))
            return;
        if (c is '+' or '-' or '*' or '/' && ViewModel.QuickSearch.Length == 0)
            return; // numeric keypad commands
        if (toCommandLine && !alt && ViewModel.QuickSearch.Length == 0)
        {
            // Configuration → Panels: typing goes to the command line (Alt+letter quick-searches)
            TypedToCommandLine?.Invoke(this, c);
            args.Handled = true;
            return;
        }

        int index = ViewModel.QuickSearchType(c);
        if (index >= 0)
            MoveCaretForQuickSearch(index);
        args.Handled = true;
    }

    /// <summary>A character typed in the panel that belongs to the command line (Configuration → Panels).</summary>
    public event EventHandler<char>? TypedToCommandLine;

    /// <summary>Backspace while quick searching; returns false when no quick search is active.</summary>
    public bool QuickSearchBackspace()
    {
        if (ViewModel == null || ViewModel.QuickSearch.Length == 0)
            return false;
        int index = ViewModel.QuickSearchBackspace();
        if (index >= 0)
            MoveCaretForQuickSearch(index);
        return true;
    }

    private void MoveCaretForQuickSearch(int index)
    {
        _quickSearchMoving = true;
        try
        {
            ApplyCaret(index, focus: true);
        }
        finally
        {
            _quickSearchMoving = false;
        }
    }

    // ---- context menu ----------------------------------------------------------------------

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        FileItemViewModel? item = (e.OriginalSource as FrameworkElement)?.DataContext as FileItemViewModel;
        if (item != null)
        {
            int index = IndexOf(item);
            if (index >= 0)
            {
                FileList.SelectedIndex = index;
                // Configuration → Panels: right-click marks (Norton style), Ctrl+right-click opens the menu
                bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
                if (!_main.Settings.Panels.RightClickOpensMenu ? !ctrl : ctrl)
                {
                    ViewModel.ToggleMark(index);
                    e.Handled = true;
                    return;
                }
            }
        }
        var (x, y) = ShellContextMenu.CursorPosition();
        ShowContextMenu(item, e.GetPosition(FileList), x, y);
        e.Handled = true;
    }

    /// <summary>A command menu (user menu, lists) at the caret item, or at the start of the address bar.</summary>
    public void ShowMenu(IReadOnlyList<MenuEntry> entries, bool atCaret)
    {
        if (ViewModel == null)
            return;
        var menu = new MenuFlyout();
        foreach (MenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }
            // entries are data (paths, user menu names); only the fixed ones are translated
            var item = new MenuFlyoutItem { Text = entry.IsEnabled ? entry.Text : Loc.T(entry.Text), IsEnabled = entry.IsEnabled };
            if (entry.Glyph != null)
                item.Icon = new FontIcon { Glyph = entry.Glyph };
            if (entry.Shortcut != null)
                item.KeyboardAcceleratorTextOverride = entry.Shortcut;
            if (entry.Invoke is { } invoke)
                item.Click += (_, _) => invoke();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => FocusList();

        FileItemViewModel? focused = ViewModel.FocusedItem;
        if (atCaret && focused != null && FileList.ContainerFromItem(focused) is ListViewItem container)
            menu.ShowAt(container, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(40, container.ActualHeight),
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            });
        else
            menu.ShowAt(HistoryButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            });
    }

    /// <summary>The Explorer "New" menu for this panel's directory, at the caret (or the mouse).</summary>
    public bool ShowNewMenu()
    {
        if (ViewModel == null || _main == null || !ViewModel.IsLocalDisk)
            return false;
        FileItemViewModel? item = ViewModel.FocusedItem;
        int x, y;
        if (item != null && FileList.ContainerFromItem(item) is ListViewItem container && XamlRoot != null)
        {
            var point = container.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(40, container.ActualHeight));
            double scale = XamlRoot.RasterizationScale;
            (x, y) = ShellContextMenu.ClientToScreen(_main.WindowHandle, (int)(point.X * scale), (int)(point.Y * scale));
        }
        else
        {
            (x, y) = ShellContextMenu.CursorPosition();
        }
        bool chosen = ShellContextMenu.ShowNewMenu(_main.WindowHandle, ViewModel.Path, x, y);
        if (!chosen)
            FocusList();
        return chosen;
    }

    /// <summary>Shift+F10 / Menu key: the context menu for the caret item, positioned at the item.</summary>
    public void ShowContextMenuForCaret()
    {
        if (ViewModel == null || _main == null)
            return;
        FileItemViewModel? item = ViewModel.FocusedItem;
        int x = 0, y = 0;
        var local = new Windows.Foundation.Point(40, 24);
        if (item != null && FileList.ContainerFromItem(item) is ListViewItem container && XamlRoot != null)
        {
            var point = container.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(40, container.ActualHeight));
            local = container.TransformToVisual(FileList).TransformPoint(new Windows.Foundation.Point(40, container.ActualHeight));
            double scale = XamlRoot.RasterizationScale;
            (x, y) = ShellContextMenu.ClientToScreen(_main.WindowHandle, (int)(point.X * scale), (int)(point.Y * scale));
        }
        else
        {
            (x, y) = ShellContextMenu.CursorPosition();
        }
        ShowContextMenu(item, local, x, y, fromMouse: false);
    }

    /// <summary>
    /// The Vibe Salamander context menu: the most used commands for the clicked items (or the directory
    /// when the empty area / ".." was clicked), and "Windows context menu" at the bottom for everything
    /// else. Shift (or Configuration → Panels) opens the Windows menu directly.
    /// </summary>
    /// <param name="position">Where to show it, relative to the list.</param>
    /// <param name="screenX">Screen position for the Windows menu.</param>
    /// <param name="fromMouse">Shift+right-click opens the Windows menu (Shift+F10 is just the menu key).</param>
    private void ShowContextMenu(FileItemViewModel? item, Windows.Foundation.Point position, int screenX, int screenY, bool fromMouse = true)
    {
        if (ViewModel == null || _main == null)
            return;
        PaneViewModel pane = ViewModel;
        if (!_main.Settings.Panels.VibeContextMenu || (fromMouse && KeyboardState.IsDown(KeyboardState.Shift)))
        {
            ShowShellMenu(item, screenX, screenY);
            return;
        }
        // like Explorer: a right-click on an unmarked item works on that item, not on the marked ones
        if (item != null && !item.IsMarked && !item.Entry.IsUpDir && pane.MarkedItems.Count > 0)
        {
            pane.RememberSelection();
            pane.MarkAll(false);
        }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, CommandId command, bool bold = false)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (bold)
                entry.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (_main.KeyMap.GetShortcutText(command) is { Length: > 0 } keys)
                entry.KeyboardAcceleratorTextOverride = keys;
            entry.Click += async (_, _) =>
            {
                _main.Activate(pane);
                await _main.ExecuteAsync(command);
            };
            menu.Items.Add(entry);
        }
        void Separator()
        {
            if (menu.Items.Count > 0 && menu.Items[^1] is not MenuFlyoutSeparator)
                menu.Items.Add(new MenuFlyoutSeparator());
        }

        bool onItem = item != null && !item.Entry.IsUpDir;
        bool local = pane.IsLocalDisk;
        if (item?.Drive != null)
        {
            // This PC: a drive
            Add("Open", "\uE8DA", CommandId.Open, bold: true);
            Add("Open in other panel", "\uE8A7", CommandId.OpenInOtherPanel);
            if (Core.Interop.DiskImages.IsMountedImage(item!.Drive!.Root))
                Add("Unmount", "\uE711", CommandId.UnmountImage);
            Separator();
            Add("Drive information", "\uE946", CommandId.DriveInformation);
            Add("Properties", "\uE90F", CommandId.Properties);
        }
        else if (onItem && pane.IsArchive)
        {
            // inside an archive: read-only
            bool directory = item!.Entry.IsDirectory;
            Add("Open", directory ? "\uE8DA" : "\uE8E5", CommandId.Open, bold: true);
            Separator();
            Add("Copy out to other panel...", "\uE8C8", CommandId.Copy);
            Add("Copy name as text", "\uE8C8", CommandId.CopyNameAsText);
        }
        else if (onItem)
        {
            int count = pane.MarkedItems.Count;
            bool several = count > 1;
            bool directory = item!.Entry.IsDirectory;
            bool archive = !directory && _main.ArchiveProvider.CanHandle(item.Entry.FullPath);
            bool image = !directory && !archive && Core.Interop.DiskImages.IsDiskImage(item.Entry.FullPath);
            if (image && _main.Settings.General.MountDiskImages)
                Add("Mount", "\uE958", CommandId.MountImage, bold: true);
            else
                Add(archive ? "Open archive" : "Open", directory || archive ? "\uE8DA" : "\uE8E5", CommandId.Open, bold: true);
            if (image && !_main.Settings.General.MountDiskImages)
                Add("Mount", "\uE958", CommandId.MountImage);
            if (directory || archive)
                Add("Open in other panel", "\uE8A7", CommandId.OpenInOtherPanel);
            if (!directory && !several)
            {
                Add("Open with...", "\uE7AC", CommandId.OpenWith);
                Add("Edit", "\uE70F", CommandId.Edit);
            }
            Separator();
            Add("Cut", "\uE8C6", CommandId.ClipboardCut);
            Add("Copy", "\uE8C8", CommandId.ClipboardCopy);
            Add("Paste", "\uE77F", CommandId.ClipboardPaste);
            Separator();
            Add(several ? $"Copy {count} items to other panel..." : "Copy to other panel...", "\uE8C8", CommandId.Copy);
            Add(several ? $"Move {count} items to other panel..." : "Move to other panel...", "\uE8DE", CommandId.Move);
            if (!several)
                Add("Rename", "\uE8AC", CommandId.QuickRename);
            Add(several ? $"Delete {count} items" : "Delete", "\uE74D", CommandId.Delete);
            Separator();
            Add(several ? "Pack into archive..." : "Pack...", "\uE7B8", CommandId.Pack);
            if (_main.Plugins.FindUnpacker(item.Entry.FullPath) != null)
                Add("Unpack...", "\uE896", CommandId.Unpack);
            Add("Copy path", "\uE8C8", CommandId.CopyFullNameAsText);
            Separator();
            Add("Properties", "\uE90F", CommandId.Properties);
        }
        else if (local)
        {
            // the directory itself (empty area or "..")
            Add("New folder", "\uE8F4", CommandId.CreateDirectory);
            Add("New text document", "\uE8A5", CommandId.EditNewFile);
            Add("New (Windows)...", "\uE710", CommandId.NewMenu);
            Separator();
            Add("Paste", "\uE77F", CommandId.ClipboardPaste);
            Add("Paste shortcut", "\uE71B", CommandId.PasteShortcut);
            Separator();
            menu.Items.Add(ViewSubmenu(pane));
            menu.Items.Add(SortSubmenu(pane));
            Add("Refresh", "\uE72C", CommandId.Refresh);
            Add("Select all", "\uE8B3", CommandId.SelectAll);
            Separator();
            Add("Open in Explorer", "\uEC50", CommandId.OpenActiveFolder);
            Add("Command prompt here", "\uE756", CommandId.CommandShell);
            if (pane.Path.Length >= 2 && pane.Path[1] == ':' && Core.Interop.DiskImages.IsMountedImage(pane.Path[..2] + "\\"))
            {
                Separator();
                Add($"Unmount {pane.Path[..2].ToUpperInvariant()}", "\uE711", CommandId.UnmountImage);
            }
        }
        else
        {
            menu.Items.Add(ViewSubmenu(pane));
            menu.Items.Add(SortSubmenu(pane));
            Add("Refresh", "\uE72C", CommandId.Refresh);
        }

        // everything else: the Windows menu (not for archives - their items aren't files on disk)
        if (!pane.IsArchive && (local || item?.Drive != null))
        {
            Separator();
            var windows = new MenuFlyoutItem
            {
                Text = "Windows context menu",
                Icon = new FontIcon { Glyph = "\uE712" },
                KeyboardAcceleratorTextOverride = "Shift+right-click",
            };
            windows.Click += (_, _) =>
            {
                // after this menu has closed: the Windows menu is modal
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowShellMenu(item, screenX, screenY));
            };
            menu.Items.Add(windows);
        }
        if (menu.Items.Count == 0)
            return;
        menu.Closed += (_, _) =>
        {
            if (!IsRenaming)
                FocusList();
        };
        Loc.Translate(menu);
        menu.ShowAt(FileList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = position,
            ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
        });
    }

    private MenuFlyoutSubItem ViewSubmenu(PaneViewModel pane)
    {
        var sub = new MenuFlyoutSubItem { Text = "View", Icon = new FontIcon { Glyph = "\uE8FD" } };
        (string Text, ViewMode Mode, CommandId Command)[] views =
        {
            ("Brief", ViewMode.Brief, CommandId.ViewBrief),
            ("Detailed", ViewMode.Detailed, CommandId.ViewDetailed),
            ("Icons", ViewMode.Icons, CommandId.ViewIcons),
            ("Thumbnails", ViewMode.Thumbnails, CommandId.ViewThumbnails),
            ("Tiles", ViewMode.Tiles, CommandId.ViewTiles),
        };
        foreach (var (text, mode, command) in views)
        {
            var entry = new RadioMenuFlyoutItem { Text = text, GroupName = "ContextView", IsChecked = pane.ViewMode == mode };
            if (_main?.KeyMap.GetShortcutText(command) is { Length: > 0 } keys)
                entry.KeyboardAcceleratorTextOverride = keys;
            CommandId c = command;
            entry.Click += async (_, _) =>
            {
                if (_main == null)
                    return;
                _main.Activate(pane);
                await _main.ExecuteAsync(c);
            };
            sub.Items.Add(entry);
        }
        return sub;
    }

    private MenuFlyoutSubItem SortSubmenu(PaneViewModel pane)
    {
        var sub = new MenuFlyoutSubItem { Text = "Sort by", Icon = new FontIcon { Glyph = "\uE8CB" } };
        (string Text, SortMode Mode, CommandId Command)[] sorts =
        {
            ("Name", SortMode.Name, CommandId.SortByName),
            ("Extension", SortMode.Extension, CommandId.SortByExtension),
            ("Date", SortMode.Date, CommandId.SortByDate),
            ("Size", SortMode.Size, CommandId.SortBySize),
        };
        foreach (var (text, mode, command) in sorts)
        {
            var entry = new RadioMenuFlyoutItem { Text = text, GroupName = "ContextSort", IsChecked = pane.SortMode == mode };
            if (_main?.KeyMap.GetShortcutText(command) is { Length: > 0 } keys)
                entry.KeyboardAcceleratorTextOverride = keys;
            CommandId c = command;
            entry.Click += async (_, _) =>
            {
                if (_main == null)
                    return;
                _main.Activate(pane);
                await _main.ExecuteAsync(c);
            };
            sub.Items.Add(entry);
        }
        return sub;
    }

    /// <summary>
    /// Explorer context menu for the marked items (when the clicked item is marked), the item itself,
    /// or the current directory (empty area / ".."). "Rename" starts the in-place rename.
    /// </summary>
    private void ShowShellMenu(FileItemViewModel? item, int x, int y)
    {
        if (ViewModel == null || _main == null || ViewModel.IsArchive)
            return; // inside an archive the items aren't on disk (and Explorer's ZIP folder could change the read-only archive)
        List<string> paths;
        if (item != null && item.IsMarked)
            paths = ViewModel.MarkedItems.Select(i => i.Entry.FullPath).ToList();
        else if (item?.Drive is { } drive)
            paths = new List<string> { drive.Root };
        else if (item != null && !item.Entry.IsUpDir)
            paths = new List<string> { item.Entry.FullPath };
        else if (ViewModel.IsLocalDisk)
            paths = new List<string> { ViewModel.Path };
        else
            return;

        bool shift = KeyboardState.IsDown(KeyboardState.Shift);
        string? verb = ShellContextMenu.Show(_main.WindowHandle, paths, x, y, shift);
        if (verb == "rename" && item != null)
        {
            _ = _main.ExecuteAsync(CommandId.QuickRename); // the rename box takes the focus
            return;
        }
        FocusList();
    }

    // ---- in-place rename (F2) --------------------------------------------------------------

    private TaskCompletionSource<string?>? _renameTcs;
    private TextBox? _renameBox;
    private bool _renameHadFocus;

    private bool IsRenaming => _renameBox != null;

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t))
                return t;
            if (FindDescendant(child, match) is T found)
                return found;
        }
        return null;
    }

    /// <summary>Turns the item's name label into an edit box; returns the new name or null when cancelled.</summary>
    public async Task<string?> EditNameInPlaceAsync(FileItemViewModel item, bool selectWholeName = false)
    {
        if (ViewModel == null || IsRenaming)
            return null;
        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();
        if (FileList.ContainerFromItem(item) is null)
        {
            // a list that was just reloaded (F7 / Shift+F4 on the new item) may realize the row a pass later
            var layoutPass = new TaskCompletionSource();
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => layoutPass.TrySetResult()))
                await layoutPass.Task;
            if (IsRenaming)
                return null;
            FileList.ScrollIntoView(item);
            FileList.UpdateLayout();
        }
        if (FileList.ContainerFromItem(item) is not ListViewItem container)
            return null;

        ViewModel.IsEditing = true; // hold back refreshes: rebuilding the rows would end the edit
        item.IsRenaming = true;
        container.UpdateLayout();
        TextBox? box = FindDescendant<TextBox>(container, b => b.Tag as string == "RenameBox");
        if (box == null)
        {
            item.IsRenaming = false;
            ViewModel.IsEditing = false;
            return null;
        }

        _renameBox = box;
        // asynchronous continuations: the code after the await unloads the box (x:Load), which must not
        // happen synchronously inside the box's own KeyDown/LostFocus handler
        _renameTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _renameHadFocus = false;
        // the row was recycled or the list rebuilt (theme, sort) under the edit: end it instead of waiting forever
        void BoxUnloaded(object sender, RoutedEventArgs e)
        {
            box.Unloaded -= BoxUnloaded;
            if (_renameBox == box && !box.IsLoaded)
                _renameTcs?.TrySetResult(null);
        }
        box.Unloaded += BoxUnloaded;
        box.FontSize = UiState.FontSize;
        if (UiState.PanelFont is { } renameFont)
            box.FontFamily = renameFont;
        else
            box.ClearValue(Control.FontFamilyProperty);
        box.Text = item.Entry.Name;
        int dot = item.Entry.IsDirectory || selectWholeName ? -1 : item.Entry.Name.LastIndexOf('.');
        // focus after menus/flyouts have closed and returned their focus
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_renameBox != box)
                return;
            box.Focus(FocusState.Programmatic);
            if (dot > 0)
                box.Select(0, dot);
            else
                box.SelectAll();
        });

        string? result;
        try
        {
            result = await _renameTcs.Task;
        }
        finally
        {
            _renameBox = null;
            _renameTcs = null;
            item.IsRenaming = false;
        }
        FocusList();
        // refreshes held back during the edit run now (the rename that follows reloads the panel anyway)
        ViewModel.IsEditing = false;
        return result;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender != _renameBox)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _renameTcs?.TrySetResult(((TextBox)sender).Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _renameTcs?.TrySetResult(null);
        }
        else if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            e.Handled = true; // keep the caret (and the edit) on this row
        }
    }

    private void RenameBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender == _renameBox)
            _renameHadFocus = true;
    }

    // Clicking elsewhere confirms the rename, like Explorer (only after the box really had the focus).
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender != _renameBox || !_renameHadFocus)
            return;
        var box = (TextBox)sender;
        // Focus that moved into a popup is not "clicking elsewhere": the box's own context menu, or a
        // question from a background transfer. The edit goes on and gets the focus back when it closes.
        if (PopupWithFocus() is { } popup)
        {
            void PopupClosed(object? s, object args)
            {
                popup.Closed -= PopupClosed;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_renameBox == box && FocusManager.GetFocusedElement(XamlRoot) != box)
                        box.Focus(FocusState.Programmatic);
                });
            }
            popup.Closed += PopupClosed;
            return;
        }
        _renameTcs?.TrySetResult(box.Text);
    }

    /// <summary>The open popup (flyout, dialog) that contains the focused element, if any.</summary>
    private Microsoft.UI.Xaml.Controls.Primitives.Popup? PopupWithFocus()
    {
        if (XamlRoot == null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
            return null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            if (popup.Child != null && IsInside(focused, popup.Child))
                return popup;
        return null;
    }

    // ---- drag & drop -----------------------------------------------------------------------

    private const string InternalPathsKey = "VibeSalamanderPaths";

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_marqueeCandidate || _marqueeActive)
        {
            e.Cancel = true; // dragging from the empty part of a row draws a selection rectangle instead
            return;
        }
        if (ViewModel == null || !ViewModel.IsLocalDisk || IsRenaming)
        {
            e.Cancel = true;
            return;
        }
        // Configuration → Panels: very short drags are accidental
        int minimum = _main?.Settings.Panels.DragCancelMilliseconds ?? 0;
        if (minimum > 0 && (DateTime.UtcNow - _pressTime).TotalMilliseconds < minimum)
        {
            e.Cancel = true;
            return;
        }
        _slowClickTimer?.Stop();
        var dragged = e.Items.OfType<FileItemViewModel>().ToList();
        // dragging a marked item drags the whole selection (Salamander)
        if (dragged.Any(i => i.IsMarked))
            dragged = ViewModel.MarkedItems.ToList();
        var paths = dragged.Where(i => !i.Entry.IsUpDir && !i.IsDrive).Select(i => i.Entry.FullPath).ToList();
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy |
                                    Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.Data.Properties[InternalPathsKey] = paths.ToArray();
        e.Data.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                request.SetData(await FileClipboard.ToStorageItemsAsync(paths));
            }
            catch (Exception)
            {
                // the drop target gets no data; nothing else to do
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    /// <summary>Drop target: the directory under the pointer, or the panel's directory.</summary>
    private string? DropTarget(DragEventArgs e)
    {
        if (ViewModel == null || !ViewModel.IsLocalDisk)
            return null;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel { Entry.IsDirectory: true, Entry.IsUpDir: false } dir)
            return dir.Entry.FullPath;
        return ViewModel.Path;
    }

    /// <summary>Explorer rules: Shift = move, Ctrl = copy, otherwise move within a drive and copy across drives.</summary>
    private static bool DecideMove(DragEventArgs e, string target)
    {
        if (KeyboardState.IsDown(KeyboardState.Shift))
            return true;
        if (KeyboardState.IsDown(KeyboardState.Control))
            return false;
        return e.DataView.Properties.TryGetValue(InternalPathsKey, out object? value) && value is string[] { Length: > 0 } internalPaths &&
               string.Equals(Path.GetPathRoot(internalPaths[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        string? target = DropTarget(e);
        if (target == null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }
        bool move = DecideMove(e, target);
        e.AcceptedOperation = move ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = (move ? "Move to " : "Copy to ") + target;
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        string? target = DropTarget(e);
        if (target == null)
            return;
        var deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<string> paths;
            if (e.DataView.Properties.TryGetValue(InternalPathsKey, out object? value) && value is string[] internalPaths)
                paths = internalPaths;
            else
                paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            bool move = DecideMove(e, target);
            // dropping into the directory the items already are in does nothing
            if (paths.Count == 0 || paths.All(p => string.Equals(Path.GetDirectoryName(p), target, StringComparison.OrdinalIgnoreCase)))
                return;
            _main.Activate(ViewModel);
            if (!await _main.ConfirmDropAsync(paths, target, move))
                return;
            await _main.TransferAsync(paths, target, move);
        }
        catch (Exception ex)
        {
            await _main.ShowErrorAsync(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---- header, drive bar, directory line --------------------------------------------------

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null || sender is not FrameworkElement { Tag: string tag })
            return;
        _main.Activate(ViewModel);
        if (Enum.TryParse(tag, out SortMode mode))
            ViewModel.SetSort(mode);
        FocusList();
    }

    private async void ThisPcButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
    }

    private async void DriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null || (sender as FrameworkElement)?.DataContext is not DriveButtonViewModel drive)
            return;
        _main.Activate(ViewModel);
        await ViewModel.ChangeDriveAsync(drive.Drive.Root);
    }

    private void DriveMenuButton_Click(object sender, RoutedEventArgs e) => ShowDriveMenu();

    private async void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        await _main.ExecuteAsync(CommandId.MaximizePanel);
        FocusList();
    }

    /// <summary>Applies Appearance / Colors / Drives options: font, colors, address bar buttons, drive bar.</summary>
    private string? _appliedFontKey;

    public void ApplyOptions()
    {
        if (ViewModel == null || _main == null)
            return;
        // the default ListViewItem style sets its own font, so the list's font isn't inherited by the rows:
        // the panel font goes into the item container style (a new style re-creates the rows)
        FileList.FontSize = UiState.FontSize;
        if (UiState.PanelFont is { } font)
            FileList.FontFamily = font;
        else
            FileList.ClearValue(Control.FontFamilyProperty);
        string fontKey = $"{UiState.PanelFont?.Source}|{UiState.FontSize}";
        if (fontKey != _appliedFontKey)
        {
            _appliedFontKey = fontKey;
            var rowStyle = new Style(typeof(ListViewItem))
            {
                BasedOn = (Style)Application.Current.Resources["VibeListItemStyle"],
            };
            rowStyle.Setters.Add(new Setter(Control.FontSizeProperty, UiState.FontSize));
            // room on the left for the selection indicator (the accent bar of the focused row) before the icon
            rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 4, 0)));
            if (UiState.PanelFont is { } rowFont)
                rowStyle.Setters.Add(new Setter(Control.FontFamilyProperty, rowFont));
            FileList.ItemContainerStyle = rowStyle;
        }
        MaximizeButton.Visibility = _main.Settings.Appearance.MaximizeButtonInAddressBar ? Visibility.Visible : Visibility.Collapsed;

        // dark schemes (terminal styles) make the whole panel dark: address bar, header, drive bar
        RequestedTheme = UiState.PanelTheme; // Default = the window's theme
        // color scheme: panel background and the focused row (lightweight styling of the list items)
        if (UiState.PanelBackground is { } background)
            FileList.Background = background;
        else
            FileList.ClearValue(Control.BackgroundProperty);
        foreach (string key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
        {
            if (UiState.FocusBackground is { } focus)
                FileList.Resources[key] = focus;
            else if (FileList.Resources.ContainsKey(key))
                FileList.Resources.Remove(key);
        }
        // re-template the rows so the new resources and the thumbnail size are used
        _appliedView = (ViewMode)(-1);
        ApplyViewMode();
        ViewModel.RefreshVisibleDrives();
        ViewModel.ApplyDriveOptions();
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        await _main.ExecuteAsync(CommandId.WorkingDirectoriesList);
    }

    /// <summary>Alt+F1 / Alt+F2 drive menu.</summary>
    public void ShowDriveMenu()
    {
        if (ViewModel == null || _main == null)
            return;
        var menu = new MenuFlyout();
        Core.Settings.DriveOptions driveOptions = _main.Settings.Drives;
        foreach (DriveButtonViewModel drive in ViewModel.VisibleDrives)
        {
            string free = drive.Drive.HasSize ? $"   {Core.Formatting.Formats.ShortSize(drive.Drive.FreeSpace)} free" : string.Empty;
            var item = new MenuFlyoutItem
            {
                Text = $"{drive.Drive.Root.TrimEnd('\\')}   {drive.Drive.Caption}{free}",
                Icon = new FontIcon { Glyph = "\uEDA2" },
            };
            string root = drive.Drive.Root;
            item.Click += async (_, _) => await ViewModel.ChangeDriveAsync(root);
            menu.Items.Add(item);
            if (drive.SeparatorAfter)
                menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var thisPc = new MenuFlyoutItem { Text = Loc.T("This PC"), Icon = new FontIcon { Glyph = "\uE7F4" } };
        thisPc.Click += async (_, _) => await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
        menu.Items.Add(thisPc);
        if (driveOptions.MenuShowDocuments)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var docs = new MenuFlyoutItem { Text = Loc.T("Documents"), Icon = new FontIcon { Glyph = "\uE8A5" } };
            docs.Click += async (_, _) => await ViewModel.NavigateAsync(documents);
            menu.Items.Add(docs);
        }
        if (driveOptions.MenuShowNetwork)
        {
            var network = new MenuFlyoutItem { Text = Loc.T("Network"), Icon = new FontIcon { Glyph = "\uE968" } };
            network.Click += (_, _) => Core.Interop.ShellOperations.OpenInExplorer("shell:NetworkPlacesFolder");
            menu.Items.Add(network);
        }
        if (driveOptions.MenuShowOtherPanelPath)
        {
            PaneViewModel other = _main.Left == ViewModel ? _main.Right : _main.Left;
            string otherPath = other.IsThisPc ? ThisPcProvider.RootPath : other.Path;
            var asOther = new MenuFlyoutItem { Text = Loc.T("As Other Panel"), Icon = new FontIcon { Glyph = "\uE8AB" }, KeyboardAcceleratorTextOverride = "Ctrl+." };
            asOther.Click += async (_, _) => await ViewModel.NavigateAsync(otherPath);
            menu.Items.Add(asOther);
        }

        // hot paths (Ctrl+Shift+digit sets one, Ctrl+digit goes there)
        IList<string> hotPaths = _main.Settings.HotPaths;
        bool header = false;
        for (int i = 1; i < hotPaths.Count && i < 10 && driveOptions.MenuShowHotPaths; i++)
        {
            string hot = hotPaths[i];
            if (string.IsNullOrEmpty(hot))
                continue;
            if (!header)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                header = true;
            }
            string hotName = i < _main.Settings.HotPathNames.Count ? _main.Settings.HotPathNames[i] : string.Empty;
            var item = new MenuFlyoutItem
            {
                Text = hotName.Length > 0 ? hotName : hot == ThisPcProvider.RootPath ? Loc.T("This PC") : hot,
                Icon = new FontIcon { Glyph = "\uE734" },
                KeyboardAcceleratorTextOverride = "Ctrl+" + i,
            };
            item.Click += async (_, _) => await ViewModel.NavigateAsync(hot);
            menu.Items.Add(item);
        }
        menu.ShowAt(DriveMenuButton);
    }

    private void DirectoryLine_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        if (e.OriginalSource is DependencyObject source && (IsInside(source, DriveMenuButton) || IsInside(source, HistoryButton) || IsInside(source, MaximizeButton)))
            return;
        BeginPathEdit();
        e.Handled = true;
    }

    /// <summary>Address bar in edit mode with the full location selected (empty-area click, Shift+F7).</summary>
    public void BeginPathEdit()
    {
        if (ViewModel == null)
            return;
        // the full location, ready to edit (Windows 11 Explorer: click the empty part of the address bar)
        PathEditor.Text = ViewModel.IsLocalDisk ? ViewModel.Path : ViewModel.IsThisPc ? Loc.T("This PC") : ViewModel.DisplayPath;
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        PathEditor.Visibility = Visibility.Visible;
        // focus after menus/flyouts have closed and returned their focus (Shift+F7 from the menu), else the
        // editor loses the focus at once and closes
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (PathEditor.Visibility != Visibility.Visible)
                return;
            PathEditor.Focus(FocusState.Programmatic);
            PathEditor.SelectAll();
        });
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container))
                return true;
        return false;
    }

    private async void PathEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel == null)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            string text = PathEditor.Text;
            EndPathEdit();
            if (text.Trim().Equals("this pc", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals(Loc.T("This PC"), StringComparison.CurrentCultureIgnoreCase))
                await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
            else if (!string.IsNullOrWhiteSpace(text))
            {
                string target = PathUtil.Resolve(ViewModel.IsLocalDisk || ViewModel.IsArchive ? ViewModel.Path : @"C:\", text);
                // Confirmations → "The directory doesn't exist. Do you want to create it?" (not for a directory in an archive)
                if (_main != null && !Directory.Exists(target) && !File.Exists(target) && !_main.ArchiveProvider.CanHandle(target) &&
                    !await _main.CreateMissingDirectoryAsync(target))
                    return;
                await ViewModel.NavigateAsync(target);
            }
            FocusList();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndPathEdit();
            FocusList();
        }
    }

    private void PathEditor_LostFocus(object sender, RoutedEventArgs e) => EndPathEdit();

    private void EndPathEdit()
    {
        PathEditor.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    // ---- address bar (breadcrumb) ---------------------------------------------------------

    /// <summary>One location in the address bar: its button and the chevron after it.</summary>
    private sealed record Crumb(string Label, string Path, Button Segment, Button Chevron);

    private readonly List<Crumb> _crumbs = new();
    private Button? _overflowButton;
    private (string Path, string Display, object Drives)? _breadcrumbKey;

    /// <summary>"This PC", the drive ("Local Disk (C:)") or share, then each folder of the path.</summary>
    private List<(string Label, string Path)> BreadcrumbSegments()
    {
        var segments = new List<(string, string)> { (Loc.T("This PC"), ThisPcProvider.RootPath) };
        if (ViewModel == null || ViewModel.IsThisPc)
            return segments;
        if (!ViewModel.IsLocalDisk && !ViewModel.IsArchive)
        {
            segments.Add((ViewModel.DisplayPath, ViewModel.Path));
            return segments;
        }

        string path = ViewModel.Path;
        string root = Path.GetPathRoot(path) ?? path;
        string rootLabel;
        if (root.Length >= 2 && root[1] == ':')
        {
            string drive = root[..2].ToUpperInvariant();
            DriveButtonViewModel? info = ViewModel.Drives.FirstOrDefault(d => d.Drive.Root.StartsWith(drive, StringComparison.OrdinalIgnoreCase));
            rootLabel = info?.Drive.Caption ?? drive;
            root = drive + "\\";
        }
        else
        {
            rootLabel = root.TrimEnd('\\'); // \\server\share
        }
        segments.Add((rootLabel, root));

        string current = root;
        foreach (string part in path[Math.Min(path.Length, Path.GetPathRoot(path)?.Length ?? 0)..]
                     .Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add((part, current));
        }
        return segments;
    }

    private Button CrumbButton(UIElement content, string? toolTip)
    {
        // like Explorer, the address bar doesn't take the keyboard focus away from the file list
        var button = new Button { Content = content, AllowFocusOnInteraction = false, IsTabStop = false };
        if (Application.Current.Resources.TryGetValue("VibeBreadcrumbButtonStyle", out object? style))
            button.Style = style as Style;
        if (toolTip != null)
            ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private void RebuildBreadcrumb()
    {
        if (ViewModel == null)
            return;
        // PropertyChanged comes several times per navigation and on every refresh (DisplayPath); rebuilding
        // only on a real change keeps the buttons (and an open subfolder menu's anchor) alive
        var key = (ViewModel.Path, ViewModel.DisplayPath, (object)ViewModel.Drives);
        if (_breadcrumbKey == key)
            return;
        _breadcrumbKey = key;
        Breadcrumb.Children.Clear();
        _crumbs.Clear();
        Brush foreground = PathForeground(ViewModel.IsActive);

        // shown when the path is too long: the leading locations move into its menu (like Explorer)
        _overflowButton = CrumbButton(new FontIcon { Glyph = "\uE712", FontSize = 12, Foreground = foreground }, "More locations");
        _overflowButton.Visibility = Visibility.Collapsed;
        _overflowButton.Click += Overflow_Click;
        Breadcrumb.Children.Add(_overflowButton);

        var segments = BreadcrumbSegments();
        for (int i = 0; i < segments.Count; i++)
        {
            var (label, path) = segments[i];
            bool last = i == segments.Count - 1;
            var text = new TextBlock
            {
                Text = label,
                Foreground = foreground,
                FontWeight = last ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 360, // a very long folder name is trimmed instead of pushing everything out
            };
            Button segment = CrumbButton(text, path == ThisPcProvider.RootPath ? Loc.T("This PC") : path);
            segment.Click += async (_, _) => await NavigateFromBreadcrumbAsync(path);

            Button chevron = CrumbButton(new FontIcon { Glyph = "\uE76C", FontSize = 8, Foreground = foreground }, null);
            chevron.Padding = new Thickness(3, 4, 3, 4);
            string? next = last ? null : segments[i + 1].Path;
            chevron.Click += async (sender, _) => await ShowSubfoldersAsync((Button)sender, path, next);

            Breadcrumb.Children.Add(segment);
            Breadcrumb.Children.Add(chevron);
            _crumbs.Add(new Crumb(label, path, segment, chevron));
        }
        UpdateBreadcrumbOverflow();
    }

    /// <summary>Active/inactive or theme change: only the text color changes.</summary>
    private void UpdateBreadcrumbForeground()
    {
        if (ViewModel == null)
            return;
        Brush foreground = PathForeground(ViewModel.IsActive);
        if (_overflowButton?.Content is FontIcon more)
            more.Foreground = foreground;
        foreach (Crumb crumb in _crumbs)
        {
            if (crumb.Segment.Content is TextBlock text)
                text.Foreground = foreground;
            if (crumb.Chevron.Content is FontIcon chevron)
                chevron.Foreground = foreground;
        }
    }

    private void BreadcrumbHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBreadcrumbOverflow();

    /// <summary>Hides leading locations (into the "..." menu) until the rest fits; the current folder always stays.</summary>
    private void UpdateBreadcrumbOverflow()
    {
        if (_overflowButton == null || _crumbs.Count == 0)
            return;
        double available = BreadcrumbHost.ActualWidth;
        if (available <= 0)
            return;
        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        double Width(UIElement element)
        {
            element.Measure(infinite);
            return element.DesiredSize.Width;
        }

        double total = 0;
        foreach (Crumb crumb in _crumbs)
        {
            crumb.Segment.Visibility = Visibility.Visible;
            crumb.Chevron.Visibility = Visibility.Visible;
            total += Width(crumb.Segment) + Width(crumb.Chevron);
        }
        _overflowButton.Visibility = Visibility.Collapsed;
        if (total <= available)
            return;

        _overflowButton.Visibility = Visibility.Visible;
        total += Width(_overflowButton);
        for (int i = 0; i < _crumbs.Count - 1 && total > available; i++)
        {
            total -= _crumbs[i].Segment.DesiredSize.Width + _crumbs[i].Chevron.DesiredSize.Width;
            _crumbs[i].Segment.Visibility = Visibility.Collapsed;
            _crumbs[i].Chevron.Visibility = Visibility.Collapsed;
        }
    }

    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (Crumb crumb in _crumbs.Where(c => c.Segment.Visibility == Visibility.Collapsed).Reverse())
        {
            var item = new MenuFlyoutItem
            {
                Text = crumb.Label,
                Icon = new FontIcon { Glyph = crumb.Path == ThisPcProvider.RootPath ? "\uE7F4" : crumb.Path.Length <= 3 ? "\uEDA2" : "\uE8B7" },
            };
            string path = crumb.Path;
            item.Click += async (_, _) => await NavigateFromBreadcrumbAsync(path);
            menu.Items.Add(item);
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task NavigateFromBreadcrumbAsync(string path)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        // going up focuses the folder we came from, like Backspace
        string? focus = (ViewModel.IsLocalDisk || ViewModel.IsArchive) && path != ThisPcProvider.RootPath ? ChildOnTheWay(path, ViewModel.Path) : null;
        await ViewModel.NavigateAsync(path, focus);
        FocusList();
    }

    /// <summary>Name of the item inside <paramref name="ancestor"/> that leads to <paramref name="path"/>, if any.</summary>
    private static string? ChildOnTheWay(string ancestor, string path)
    {
        string a = ancestor.TrimEnd('\\') + "\\";
        if (!path.StartsWith(a, StringComparison.OrdinalIgnoreCase) || path.Length <= a.Length)
            return null;
        return path[a.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    /// <summary>The chevron after a location: its subfolders (drives after "This PC"), the next one on the path in bold.</summary>
    private async Task ShowSubfoldersAsync(Button anchor, string path, string? next)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        var menu = new MenuFlyout();
        if (path == ThisPcProvider.RootPath)
        {
            foreach (DriveButtonViewModel drive in ViewModel.Drives)
            {
                string root = drive.Drive.Root;
                var item = new MenuFlyoutItem { Text = drive.Drive.Caption, Icon = new FontIcon { Glyph = "\uEDA2" } };
                if (next != null && root.StartsWith(next.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                item.Click += async (_, _) => await NavigateFromBreadcrumbAsync(root);
                menu.Items.Add(item);
            }
        }
        else
        {
            bool showHidden = _main.Settings.ShowHidden;
            List<string> folders = _main.ArchiveProvider.CanHandle(path)
                ? await _main.ArchiveProvider.ListDirectoriesAsync(path) // a directory inside an archive (or the archive itself)
                : await Task.Run(() =>
            {
                try
                {
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                    };
                    return Directory.EnumerateDirectories(path, "*", options)
                        .Select(d => Path.GetFileName(d))
                        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                        .Take(500)
                        .ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    return new List<string>();
                }
            });
            if (folders.Count == 0)
                menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("(no subfolders)"), IsEnabled = false });
            string? nextName = next != null ? Path.GetFileName(next.TrimEnd('\\')) : null;
            foreach (string name in folders)
            {
                string target = Path.Combine(path, name);
                var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = "\uE8B7" } };
                if (nextName != null && string.Equals(name, nextName, StringComparison.OrdinalIgnoreCase))
                    item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                item.Click += async (_, _) => await NavigateFromBreadcrumbAsync(target);
                menu.Items.Add(item);
            }
        }
        if (anchor.XamlRoot == null)
            return; // the address bar was rebuilt while the folders were read
        menu.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
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
        mode.ToString() == column ? (descending ? "\uE70D" : "\uE70E") : string.Empty;

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
