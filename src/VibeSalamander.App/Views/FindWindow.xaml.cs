// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Search;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>The widths of the result columns, shared by the header and every row (resized at the header).</summary>
public sealed class FindColumnLayout : System.ComponentModel.INotifyPropertyChanged
{
    private double _name, _size, _date;

    public FindColumnLayout(Core.Settings.FindColumnWidths widths)
    {
        _name = Clamp(widths.Name);
        _size = Clamp(widths.Size);
        _date = Clamp(widths.Date);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public GridLength NameWidth => new(_name);
    public GridLength SizeWidth => new(_size);
    public GridLength DateWidth => new(_date);

    private static double Clamp(double width) => double.IsFinite(width) ? Math.Clamp(width, 40, 1200) : 100;

    /// <summary>"Name", "Size" or "Date".</summary>
    public double Get(string column) => column switch { "Name" => _name, "Size" => _size, _ => _date };

    public void Set(string column, double width)
    {
        width = Clamp(width);
        switch (column)
        {
            case "Name": _name = width; break;
            case "Size": _size = width; break;
            default: _date = width; break;
        }
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(column + "Width"));
    }

    public static double Default(string column) => column switch
    {
        "Name" => Core.Settings.FindColumnWidths.DefaultName,
        "Size" => Core.Settings.FindColumnWidths.DefaultSize,
        _ => Core.Settings.FindColumnWidths.DefaultDate,
    };

    public void SaveTo(Core.Settings.FindColumnWidths widths)
    {
        widths.Name = _name;
        widths.Size = _size;
        widths.Date = _date;
    }
}

/// <summary>One search result row.</summary>
public sealed class FindResult
{
    public FindResult(FileEntry entry, FindColumnLayout columns)
    {
        Entry = entry;
        Columns = columns;
    }

    public FileEntry Entry { get; }
    public FindColumnLayout Columns { get; }
    public string Name => Entry.Name;
    public string Directory => Path.GetDirectoryName(Entry.FullPath) ?? string.Empty;
    public string SizeText => Entry.IsDirectory ? "DIR" : Formats.ExactSize(Entry.Size);
    public string DateText => $"{Formats.Date(Entry.LastWrite)} {Formats.Time(Entry.LastWrite)}";
    public string Glyph => Entry.IsDirectory ? "" : "";
}

/// <summary>
/// Find Files and Directories (Alt+F7). Non-modal, like Salamander's Find window. The results sort by
/// clicking a column header; right-click gives the Vibe context menu for the found item.
/// </summary>
public sealed partial class FindWindow : Window
{
    private readonly Func<string, string?, Task> _focusInPanel;
    private readonly MainViewModel _main;
    private ObservableCollection<FindResult> _results = new();
    private readonly List<FindResult> _pending = new();
    private readonly object _pendingLock = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _flushTimer;
    private CancellationTokenSource? _cts;

    private readonly Func<Core.Settings.ConfirmationOptions> _confirm;
    private readonly Dialogs _dialogs;
    private bool _closeConfirmed;

    /// <param name="startDirectory">Initial "Look in" directory (the active panel's path).</param>
    /// <param name="focusInPanel">Navigates the active panel to (directory, name to focus or null) and shows the main window.</param>
    public FindWindow(string startDirectory, ElementTheme theme, MainViewModel main, Func<string, string?, Task> focusInPanel)
    {
        _main = main;
        Layout = new FindColumnLayout(main.Settings.FindColumns);
        InitializeComponent();
        _focusInPanel = focusInPanel;
        _confirm = () => main.Settings.Confirm;
        _dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme, () => AppWindow.Id);
        Loc.Translate(RootGrid);
        Title = Loc.T(Title);
        AppWindow.Closing += async (_, args) =>
        {
            // Configuration → Confirmations → "Do you want to close Find?"
            if (_closeConfirmed || !_confirm().CloseFind || (_results.Count == 0 && _cts == null))
                return;
            args.Cancel = true;
            await CloseWithConfirmationAsync();
        };
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarText);
        RootGrid.RequestedTheme = theme;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 640));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);

        LookInBox.Text = startDirectory;
        Results.ItemsSource = _results;
        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(200);
        _flushTimer.Tick += (_, _) => Flush();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _flushTimer.Stop(); // a running timer would keep the closed window alive
        };
        RootGrid.Loaded += (_, _) => { NamedBox.Focus(FocusState.Programmatic); NamedBox.SelectAll(); };
    }

    public void SetLookIn(string directory) => LookInBox.Text = directory;

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            await StopWithConfirmationAsync(); // "Stop"
            return;
        }
        await RunSearchAsync();
    }

    private async Task<bool> ConfirmAsync(string message, string button)
    {
        ContentDialog dialog = _dialogs.Create("Find", new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = button;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await _dialogs.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Confirmations → "Do you want to stop searching?"</summary>
    private async Task StopWithConfirmationAsync()
    {
        if (_cts == null)
            return;
        if (_confirm().StopFind && !await ConfirmAsync("Do you want to stop searching?", "Stop"))
            return;
        _cts?.Cancel();
    }

    private async Task CloseWithConfirmationAsync()
    {
        if (!await ConfirmAsync("Do you want to close Find?", "Close"))
            return;
        _closeConfirmed = true;
        _cts?.Cancel();
        Close();
    }

    private async Task RunSearchAsync()
    {
        string directory = LookInBox.Text.Trim();
        if (!System.IO.Directory.Exists(directory))
        {
            StatusText.Text = Loc.F("Directory not found: {0}", directory);
            return;
        }
        var options = new SearchOptions
        {
            Directory = directory,
            Mask = new WildcardMask(string.IsNullOrWhiteSpace(NamedBox.Text) ? "*.*" : NamedBox.Text),
            IncludeSubdirectories = SubdirsBox.IsChecked == true,
            IncludeDirectories = DirectoriesBox.IsChecked == true,
            IncludeHidden = HiddenBox.IsChecked == true,
            ContainingText = string.IsNullOrEmpty(ContainingBox.Text) ? null : ContainingBox.Text,
            MatchCase = MatchCaseBox.IsChecked == true,
        };

        _results.Clear();
        _cts = new CancellationTokenSource();
        FindButton.Content = Loc.T("Stop");
        Busy.IsActive = true;
        _flushTimer.Start();
        var progress = new Progress<string>(dir => DispatcherQueue.TryEnqueue(() => { if (_cts != null) StatusText.Text = Loc.F("Searching: {0}", dir); }));
        string status;
        try
        {
            int count = await FileSearcher.SearchAsync(options, entry =>
            {
                lock (_pendingLock)
                    _pending.Add(new FindResult(entry, Layout));
            }, progress, _cts.Token);
            status = count == 1 ? Loc.T("1 item found") : Loc.F("{0} items found", count.ToString("N0"));
        }
        catch (OperationCanceledException)
        {
            status = Loc.T("Search stopped");
        }
        _flushTimer.Stop();
        Flush();
        _cts.Dispose();
        _cts = null;
        FindButton.Content = Loc.T("Find");
        Busy.IsActive = false;
        StatusText.Text = status + " " + Loc.F("({0} listed)", _results.Count.ToString("N0"));
        if (_results.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Moves found items to the list in batches (keeps the UI responsive for large result sets).</summary>
    private void Flush()
    {
        List<FindResult> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
                return;
            batch = new List<FindResult>(_pending);
            _pending.Clear();
        }
        if (_sort == FindSort.None)
        {
            foreach (FindResult r in batch)
                _results.Add(r);
        }
        else if (batch.Count > 500 || _results.Count > 20_000)
        {
            // many at once: sorting everything again is cheaper than inserting one by one
            ShowSorted(_results.Concat(batch));
        }
        else
        {
            Comparison<FindResult> compare = CurrentComparison();
            foreach (FindResult r in batch)
                _results.Insert(InsertionIndex(r, compare), r);
        }
    }

    // ---- column widths (grips in the header) ---------------------------------------------------

    /// <summary>The result columns' widths (x:Bind in the header; the rows share it).</summary>
    public FindColumnLayout Layout { get; }

    private string? _resizing;
    private double _resizeStartX, _resizeStartWidth;

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column } grip)
            return;
        _resizing = column;
        _resizeStartX = e.GetCurrentPoint(HeaderGrid).Position.X;
        _resizeStartWidth = Layout.Get(column);
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing is not string column)
            return;
        if (!e.Pointer.IsInContact)
        {
            _resizing = null; // capture lost without a release: hovering must not resize
            return;
        }
        // Name's grip is on its right edge, the others' on their left edge
        double dx = e.GetCurrentPoint(HeaderGrid).Position.X - _resizeStartX;
        Layout.Set(column, column == "Name" ? _resizeStartWidth + dx : _resizeStartWidth - dx);
        e.Handled = true;
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing != null)
            SaveColumnWidths();
        _resizing = null;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Grip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string column)
        {
            Layout.Set(column, FindColumnLayout.Default(column));
            SaveColumnWidths();
        }
        e.Handled = true;
    }

    private void SaveColumnWidths()
    {
        Layout.SaveTo(_main.Settings.FindColumns);
        _main.SaveSettings();
    }

    // ---- sorting (column headers) --------------------------------------------------------------

    private enum FindSort { None, Name, Path, Size, Date }

    private FindSort _sort = FindSort.None;
    private bool _descending;

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out FindSort sort))
            return;
        // the same column again: the other direction
        _descending = sort == _sort && !_descending;
        _sort = sort;
        NameArrow.Glyph = Arrow(FindSort.Name);
        PathArrow.Glyph = Arrow(FindSort.Path);
        SizeArrow.Glyph = Arrow(FindSort.Size);
        DateArrow.Glyph = Arrow(FindSort.Date);
        ShowSorted(_results.ToList());
    }

    private string Arrow(FindSort column) => column != _sort ? string.Empty : _descending ? "\uE70D" : "\uE70E";

    /// <summary>The results in the chosen order; the selected one stays selected and in view.</summary>
    private void ShowSorted(IEnumerable<FindResult> items)
    {
        var selected = Results.SelectedItem as FindResult;
        var list = items.ToList();
        list.Sort(CurrentComparison());
        _results = new ObservableCollection<FindResult>(list);
        Results.ItemsSource = _results;
        if (selected != null)
        {
            Results.SelectedItem = selected;
            Results.ScrollIntoView(selected);
        }
    }

    /// <summary>Directories before files when sorting by size; names break ties.</summary>
    private Comparison<FindResult> CurrentComparison()
    {
        StringComparer text = StringComparer.CurrentCultureIgnoreCase;
        Comparison<FindResult> ascending = _sort switch
        {
            FindSort.Path => (a, b) => text.Compare(a.Directory, b.Directory) is int c and not 0 ? c : text.Compare(a.Name, b.Name),
            FindSort.Size => (a, b) => (a.Entry.IsDirectory ? -1 : a.Entry.Size).CompareTo(b.Entry.IsDirectory ? -1 : b.Entry.Size) is int c and not 0
                ? c : text.Compare(a.Name, b.Name),
            FindSort.Date => (a, b) => a.Entry.LastWrite.CompareTo(b.Entry.LastWrite) is int c and not 0 ? c : text.Compare(a.Name, b.Name),
            _ => (a, b) => text.Compare(a.Name, b.Name) is int c and not 0 ? c : text.Compare(a.Directory, b.Directory),
        };
        return _descending ? (a, b) => ascending(b, a) : ascending;
    }

    /// <summary>Where a new result goes in the sorted list (after its equals: found order is kept).</summary>
    private int InsertionIndex(FindResult item, Comparison<FindResult> compare)
    {
        int low = 0, high = _results.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (compare(_results[middle], item) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    // ---- context menu ------------------------------------------------------------------------------

    private IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>
    /// Right-click / menu key on a result: the Vibe Salamander context menu for it, with "Open file
    /// location" and "Copy file path". Shift+right-click (or Configuration → Panels) opens the Windows menu.
    /// </summary>
    private void Results_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        FindResult? result = (args.OriginalSource as FrameworkElement)?.DataContext as FindResult ?? Results.SelectedItem as FindResult;
        if (result == null)
            return;
        Results.SelectedItem = result;
        Windows.Foundation.Point position;
        if (!args.TryGetPosition(Results, out position))
        {
            // the menu key: at the selected row
            position = Results.ContainerFromItem(result) is UIElement row
                ? row.TransformToVisual(Results).TransformPoint(new Windows.Foundation.Point(24, 20))
                : new Windows.Foundation.Point(24, 24);
        }
        bool fromMouse = args.TryGetPosition(Results, out _);
        if (!_main.Settings.Panels.VibeContextMenu || (fromMouse && KeyboardState.IsDown(KeyboardState.Shift)))
        {
            ShowWindowsMenu(result);
            return;
        }
        BuildMenu(result).ShowAt(Results, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = position,
            ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
        });
    }

    private MenuFlyout BuildMenu(FindResult result)
    {
        FileEntry entry = result.Entry;
        string path = entry.FullPath;
        bool directory = entry.IsDirectory;
        bool archive = !directory && _main.ArchiveProvider.CanHandle(path);
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action click, bool bold = false, string? keys = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (bold)
                item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (keys != null)
                item.KeyboardAcceleratorTextOverride = keys;
            item.Click += (_, _) =>
            {
                try
                {
                    click();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    StatusText.Text = ex.Message;
                }
            };
            menu.Items.Add(item);
        }
        void Separator()
        {
            if (menu.Items.Count > 0 && menu.Items[^1] is not MenuFlyoutSeparator)
                menu.Items.Add(new MenuFlyoutSeparator());
        }

        // directories and archives open in the focused panel, files with their program
        if (directory || archive)
            Add(archive ? "Open archive" : "Open", "\uE8DA", () => _ = _focusInPanel(path, null), bold: true);
        else
            Add("Open", "\uE8E5", () => _main.OpenFoundFile(entry), bold: true);
        Add("Open file location", "\uE838", () => _ = _focusInPanel(result.Directory, result.Name), keys: "Enter");
        if (!directory)
        {
            Add("Open with...", "\uE7AC", () => Core.Interop.ShellOperations.OpenWith(WindowHandle, path));
            Add("View", "\uE890", () => _main.ViewFoundFile(entry));
            Add("Edit", "\uE70F", () => _main.EditFoundFile(entry));
            if (Core.Interop.ScriptRunner.HostOf(path) is var host && host != Core.Interop.ScriptHost.None)
                Add(host == Core.Interop.ScriptHost.PowerShell ? "Run with PowerShell" : "Run in cmd", "\uE756", () => Core.Interop.ScriptRunner.Run(path));
        }
        Separator();
        Add("Cut", "\uE8C6", () => _ = FileClipboard.SetFilesAsync(new[] { path }, cut: true));
        Add("Copy", "\uE8C8", () => _ = FileClipboard.SetFilesAsync(new[] { path }, cut: false));
        Separator();
        Add("Copy file path", "\uE8C8", () => CopyText(path));
        Add("Copy name as text", "\uE8C8", () => CopyText(entry.Name));
        Separator();
        Add("Delete", "\uE74D", () => _ = DeleteAsync(result));
        Separator();
        Add("Properties", "\uE90F", () => Core.Interop.ShellOperations.ShowProperties(WindowHandle, path));
        var windows = new MenuFlyoutItem
        {
            Text = "Windows context menu",
            Icon = new FontIcon { Glyph = "\uE712" },
            KeyboardAcceleratorTextOverride = "Shift+right-click",
        };
        // after this menu has closed: the Windows menu is modal
        windows.Click += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowWindowsMenu(result));
        menu.Items.Add(windows);
        menu.Closed += (_, _) => Results.Focus(FocusState.Programmatic);
        Loc.Translate(menu);
        return menu;
    }

    private void CopyText(string text)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        StatusText.Text = Loc.F("Copied: {0}", text);
    }

    private void ShowWindowsMenu(FindResult result)
    {
        var (x, y) = ShellContextMenu.CursorPosition();
        ShellContextMenu.Show(WindowHandle, new[] { result.Entry.FullPath }, x, y, KeyboardState.IsDown(KeyboardState.Shift));
        RemoveIfGone(result); // the Windows menu may have deleted or moved it
        Results.Focus(FocusState.Programmatic);
    }

    /// <summary>To the Recycle Bin (Configuration → Confirmations asks first); the row goes when the item has gone.</summary>
    private async Task DeleteAsync(FindResult result)
    {
        string path = result.Entry.FullPath;
        if (_main.Settings.ConfirmRecycle && !await ConfirmAsync(Loc.F(result.Entry.IsDirectory
                ? "Do you want to move the directory \"{0}\" to the Recycle Bin?"
                : "Do you want to move the file \"{0}\" to the Recycle Bin?", result.Name), Loc.T("Delete")))
            return;
        await Core.Interop.ShellOperations.DeleteAsync(WindowHandle, new[] { path }, permanent: false);
        RemoveIfGone(result);
    }

    private void RemoveIfGone(FindResult result)
    {
        string path = result.Entry.FullPath;
        if (!File.Exists(path) && !System.IO.Directory.Exists(path))
            _results.Remove(result);
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FocusButton.IsEnabled = Results.SelectedItem is FindResult;

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await FocusSelectedAsync();

    private async void FocusButton_Click(object sender, RoutedEventArgs e) => await FocusSelectedAsync();

    private async Task FocusSelectedAsync()
    {
        if (Results.SelectedItem is FindResult r)
            await _focusInPanel(r.Directory, r.Name);
    }


    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is ListViewItem or ListView)
                await FocusSelectedAsync();
            else if (_cts == null)
                await RunSearchAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_cts != null)
                await StopWithConfirmationAsync();
            else if (_confirm().CloseFind && _results.Count > 0)
                await CloseWithConfirmationAsync(); // Window.Close() doesn't raise AppWindow.Closing: ask here
            else
                Close();
        }
    }
}
