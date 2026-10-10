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

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is FileItemViewModel { IsDrive: true } ? DriveTemplate : FileTemplate;

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
    }

    public PaneViewModel? ViewModel { get; private set; }
    private MainViewModel? _main;

    public void Initialize(PaneViewModel viewModel, MainViewModel main)
    {
        ViewModel = viewModel;
        _main = main;
        viewModel.ListingChanged += (_, e) => ApplyCaret(e.CaretIndex, focus: viewModel.IsActive);
        Bindings.Update();
    }

    /// <summary>Re-evaluates the x:Bind bindings (after theme changes).</summary>
    public void RefreshBindings() => Bindings.Update();

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
        if (focus && RenameBox.Visibility != Visibility.Visible)
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
            int index = FileList.SelectedIndex;
            if (index >= 0 && FileList.ContainerFromIndex(index) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            else
                FileList.Focus(FocusState.Programmatic);
        });
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && FileList.SelectedIndex >= 0)
        {
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
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item)
            return;
        int index = IndexOf(item);
        if (index < 0)
            return;
        _main.Activate(ViewModel);
        ViewModel.CaretIndex = index;
        await _main.ExecuteAsync(CommandId.Open);
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is FileItemViewModel item && _main != null)
            item.EnsureIcon(_main.Icons);
    }

    private void List_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null)
            return;
        bool ctrl = Services.KeyboardState.IsDown(Services.KeyboardState.Control);
        if (!ctrl || !e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
            return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel item)
            ViewModel.ToggleMark(IndexOf(item));
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
        if (ViewModel == null)
            return;
        char c = args.Character;
        if (char.IsControl(c))
            return;
        if (c == ' ' && ViewModel.QuickSearch.Length == 0)
            return; // Space marks the item
        bool ctrlOrAlt =
            Services.KeyboardState.IsDown(Services.KeyboardState.Control) ||
            Services.KeyboardState.IsDown(Services.KeyboardState.Alt);
        if (ctrlOrAlt)
            return;
        if (c is '+' or '-' or '*' or '/' && ViewModel.QuickSearch.Length == 0)
            return; // numeric keypad commands

        int index = ViewModel.QuickSearchType(c);
        if (index >= 0)
            MoveCaretForQuickSearch(index);
        args.Handled = true;
    }

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
                FileList.SelectedIndex = index;
        }
        var (x, y) = ShellContextMenu.CursorPosition();
        ShowShellMenu(item, x, y);
        e.Handled = true;
    }

    /// <summary>Shift+F10 / Menu key: the context menu for the caret item, positioned at the item.</summary>
    public void ShowContextMenuForCaret()
    {
        if (ViewModel == null || _main == null)
            return;
        FileItemViewModel? item = ViewModel.FocusedItem;
        int x = 0, y = 0;
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
        ShowShellMenu(item, x, y);
    }

    /// <summary>
    /// Explorer context menu for the marked items (when the clicked item is marked), the item itself,
    /// or the current directory (empty area / ".."). "Rename" starts the in-place rename.
    /// </summary>
    private void ShowShellMenu(FileItemViewModel? item, int x, int y)
    {
        if (ViewModel == null || _main == null)
            return;
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
    private bool _renameHadFocus;

    /// <summary>Shows an edit box over the item's name; returns the new name or null when cancelled.</summary>
    public async Task<string?> EditNameInPlaceAsync(FileItemViewModel item)
    {
        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();
        if (FileList.ContainerFromItem(item) is not ListViewItem container)
            return null;

        var origin = container.TransformToVisual(RenameLayer).TransformPoint(new Windows.Foundation.Point(0, 0));
        const double iconColumn = 22 + 6;
        RenameBox.FontSize = UiState.FontSize;
        RenameBox.Text = item.Entry.Name;
        RenameBox.Width = Math.Max(160, Math.Min(container.ActualWidth - iconColumn - 8, 520));
        RenameBox.Height = Math.Max(UiState.RowHeight + 4, 24);
        Canvas.SetLeft(RenameBox, origin.X + iconColumn - 6);
        Canvas.SetTop(RenameBox, origin.Y + (container.ActualHeight - RenameBox.Height) / 2);
        RenameBox.Visibility = Visibility.Visible;

        _renameTcs = new TaskCompletionSource<string?>();
        _renameHadFocus = false;
        int dot = item.Entry.IsDirectory ? -1 : item.Entry.Name.LastIndexOf('.');
        // focus after menus/flyouts have closed and returned their focus
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            RenameBox.Focus(FocusState.Programmatic);
            if (dot > 0)
                RenameBox.Select(0, dot);
            else
                RenameBox.SelectAll();
        });

        string? result = await _renameTcs.Task;
        RenameBox.Visibility = Visibility.Collapsed;
        FocusList();
        return result;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _renameTcs?.TrySetResult(RenameBox.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _renameTcs?.TrySetResult(null);
        }
    }

    private void RenameBox_GotFocus(object sender, RoutedEventArgs e) => _renameHadFocus = true;

    // Clicking elsewhere confirms the rename, like Explorer (only after the box really had the focus).
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_renameHadFocus)
            _renameTcs?.TrySetResult(RenameBox.Text);
    }

    // ---- drag & drop -----------------------------------------------------------------------

    private const string InternalPathsKey = "VibeSalamanderPaths";

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (ViewModel == null || !ViewModel.IsLocalDisk)
        {
            e.Cancel = true;
            return;
        }
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

    /// <summary>Alt+F1 / Alt+F2 drive menu.</summary>
    public void ShowDriveMenu()
    {
        if (ViewModel == null || _main == null)
            return;
        var menu = new MenuFlyout();
        foreach (DriveButtonViewModel drive in ViewModel.Drives)
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
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var thisPc = new MenuFlyoutItem { Text = "This PC", Icon = new FontIcon { Glyph = "\uE7F4" } };
        thisPc.Click += async (_, _) => await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
        menu.Items.Add(thisPc);
        menu.ShowAt(DriveMenuButton);
    }

    private void DirectoryLine_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        if (e.OriginalSource is DependencyObject source && IsInside(source, DriveMenuButton))
            return;
        PathEditor.Text = ViewModel.IsLocalDisk ? ViewModel.Path : string.Empty;
        PathText.Visibility = Visibility.Collapsed;
        PathEditor.Visibility = Visibility.Visible;
        PathEditor.Focus(FocusState.Programmatic);
        PathEditor.SelectAll();
        e.Handled = true;
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
            if (text.Trim().Equals("this pc", StringComparison.OrdinalIgnoreCase))
                await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
            else if (!string.IsNullOrWhiteSpace(text))
                await ViewModel.NavigateAsync(PathUtil.Resolve(ViewModel.IsLocalDisk ? ViewModel.Path : @"C:\", text));
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
        PathText.Visibility = Visibility.Visible;
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
}
