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
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PaneViewModel.DisplayPath) or nameof(PaneViewModel.Path) or nameof(PaneViewModel.Drives))
                RebuildBreadcrumb();
            else if (e.PropertyName == nameof(PaneViewModel.IsActive))
                UpdateBreadcrumbForeground(); // not a rebuild: a chevron click activates the panel, and its menu needs the button
        };
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
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item || item.IsRenaming)
            return; // double-clicking inside the rename box selects a word
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
        if (ViewModel == null || IsRenaming)
            return; // typing goes to the rename box
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
    public async Task<string?> EditNameInPlaceAsync(FileItemViewModel item)
    {
        if (ViewModel == null || IsRenaming)
            return null;
        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();
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
        box.Text = item.Entry.Name;
        int dot = item.Entry.IsDirectory ? -1 : item.Entry.Name.LastIndexOf('.');
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
        // the full location, ready to edit (Windows 11 Explorer: click the empty part of the address bar)
        PathEditor.Text = ViewModel.IsLocalDisk ? ViewModel.Path : ViewModel.IsThisPc ? "This PC" : ViewModel.DisplayPath;
        BreadcrumbHost.Visibility = Visibility.Collapsed;
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
        var segments = new List<(string, string)> { ("This PC", ThisPcProvider.RootPath) };
        if (ViewModel == null || ViewModel.IsThisPc)
            return segments;
        if (!ViewModel.IsLocalDisk)
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
            Button segment = CrumbButton(text, path == ThisPcProvider.RootPath ? "This PC" : path);
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
        string? focus = ViewModel.IsLocalDisk && path != ThisPcProvider.RootPath ? ChildOnTheWay(path, ViewModel.Path) : null;
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
            List<string> folders = await Task.Run(() =>
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
                menu.Items.Add(new MenuFlyoutItem { Text = "(no subfolders)", IsEnabled = false });
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
}
