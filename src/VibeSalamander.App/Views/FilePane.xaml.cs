using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
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
        if (focus)
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
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel item)
        {
            int index = IndexOf(item);
            if (index >= 0)
                FileList.SelectedIndex = index;
        }
        _main.Activate(ViewModel);

        var menu = new MenuFlyout();
        void Add(string text, string glyph, CommandId command)
        {
            var mi = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = _main.CanExecute(command),
            };
            string? shortcut = _main.KeyMap.GetShortcutText(command);
            if (shortcut != null)
                mi.KeyboardAcceleratorTextOverride = shortcut;
            mi.Click += async (_, _) => await _main.ExecuteAsync(command);
            menu.Items.Add(mi);
        }
        Add("Open", "\uE8E5", CommandId.Open);
        Add("Open in other panel", "\uE8A7", CommandId.OpenInOtherPanel);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("View", "\uE890", CommandId.View);
        Add("Edit", "\uE70F", CommandId.Edit);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Copy...", "\uE8C8", CommandId.Copy);
        Add("Move/Rename...", "\uE8DE", CommandId.Move);
        Add("Rename...", "\uE8AC", CommandId.QuickRename);
        Add("Delete", "\uE74D", CommandId.Delete);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Copy full name as text", "\uE8C8", CommandId.CopyFullNameAsText);
        Add("Properties", "\uE946", CommandId.Properties);
        menu.ShowAt(FileList, e.GetPosition(FileList));
        e.Handled = true;
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
