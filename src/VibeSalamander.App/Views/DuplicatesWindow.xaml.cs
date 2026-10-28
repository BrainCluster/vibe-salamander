// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VibeSalamander.Core;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Search;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>A row of the duplicates list: a group's header or one of its files.</summary>
public abstract class DuplicateRow
{
}

public sealed class DuplicateHeaderRow : DuplicateRow
{
    public DuplicateHeaderRow(DuplicateGroup group)
    {
        Group = group;
        Title = Loc.F("{0} files", group.Files.Count);
        Detail = group.Size >= 0
            ? Loc.F("{0} each  ·  {1} could be freed", Formats.ShortSize(group.Size), Formats.ShortSize(group.Wasted))
            : group.Files[0].Name;
    }

    public DuplicateGroup Group { get; }
    public string Title { get; }
    public string Detail { get; }
}

public sealed class DuplicateFileRow : DuplicateRow, System.ComponentModel.INotifyPropertyChanged
{
    private bool _checked;

    public DuplicateFileRow(FileEntry entry, DuplicateGroup group)
    {
        Entry = entry;
        Group = group;
    }

    public FileEntry Entry { get; }
    public DuplicateGroup Group { get; }
    public string Name => Entry.Name;
    public string Folder => Path.GetDirectoryName(Entry.FullPath) ?? string.Empty;
    public string DateText => $"{Formats.Date(Entry.LastWrite)} {Formats.Time(Entry.LastWrite)}";

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? CheckedChanged;
}

public sealed class DuplicateRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is DuplicateHeaderRow ? HeaderTemplate : FileTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Find Duplicates (Alt+Shift+F7): files with the same content (or name, or size) in one or more folders, grouped,
/// the groups that waste the most space first. Select the copies to go (all but the newest, ...) and delete them to
/// the Recycle Bin, or list them in a panel.
/// </summary>
public sealed partial class DuplicatesWindow : Window
{
    private readonly MainViewModel _main;
    private readonly Func<string, string?, Task> _focusInPanel;
    private readonly Dialogs _dialogs;
    private readonly ObservableCollection<DuplicateRow> _rows = new();
    private IReadOnlyList<DuplicateGroup> _groups = Array.Empty<DuplicateGroup>();
    private CancellationTokenSource? _cts;

    public DuplicatesWindow(string startDirectory, ElementTheme theme, MainViewModel main, Func<string, string?, Task> focusInPanel)
    {
        _main = main;
        _focusInPanel = focusInPanel;
        InitializeComponent();
        _dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme, () => AppWindow.Id);
        Loc.Translate(RootGrid);
        if (SelectButton.Flyout is MenuFlyout menu)
            Loc.Translate(menu);
        Title = Loc.T(Title);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        RootGrid.RequestedTheme = theme;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 720));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);

        LookInBox.Text = startDirectory;
        string by = main.Settings.DuplicatesBy ?? "Content";
        ContentBox.IsChecked = by.Contains("Content", StringComparison.Ordinal);
        NameBox.IsChecked = by.Contains("Name", StringComparison.Ordinal);
        SizeBox.IsChecked = by.Contains("Size", StringComparison.Ordinal);
        Results.ItemsSource = _rows;
        Closed += (_, _) => _cts?.Cancel();
        RootGrid.Loaded += (_, _) => FindButton.Focus(FocusState.Programmatic);
    }

    public void SetLookIn(string directory) => LookInBox.Text = directory;

    private IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        string? folder = await _dialogs.PickFolderAsync();
        if (folder == null)
            return;
        string current = LookInBox.Text.Trim().TrimEnd(';');
        LookInBox.Text = current.Length == 0 ? folder : current + ";" + folder;
    }

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }
        await RunAsync();
    }

    private DuplicateKind Kind =>
        (ContentBox.IsChecked == true ? DuplicateKind.Content : 0) |
        (NameBox.IsChecked == true ? DuplicateKind.Name : 0) |
        (SizeBox.IsChecked == true ? DuplicateKind.Size : 0);

    private async Task RunAsync()
    {
        IReadOnlyList<string> folders = SearchForm.Folders(LookInBox.Text);
        string? missing = folders.FirstOrDefault(f => !Directory.Exists(f));
        DuplicateKind kind = Kind;
        long? min = MinSizeBox.Text.Trim().Length == 0 ? 0 : SpeedLimits.ParseBytes(MinSizeBox.Text, 1);
        StatusText.Text = folders.Count == 0 ? Loc.T("Choose where to look.")
            : missing != null ? Loc.F("Directory not found: {0}", missing)
            : kind == 0 ? Loc.T("Choose what makes files the same.")
            : min == null ? Loc.F("\"{0}\" isn't a size (like 10 KB or 2.5 GB).", MinSizeBox.Text)
            : string.Empty;
        if (StatusText.Text.Length > 0)
            return;
        _main.Settings.DuplicatesBy = string.Join(", ", new[] { kind.HasFlag(DuplicateKind.Content) ? "Content" : null,
            kind.HasFlag(DuplicateKind.Name) ? "Name" : null, kind.HasFlag(DuplicateKind.Size) ? "Size" : null }.Where(x => x != null));
        _main.SaveSettings();

        var criteria = new DuplicateCriteria
        {
            Directories = folders,
            Mask = new WildcardMask(string.IsNullOrWhiteSpace(NamedBox.Text) ? "*.*" : NamedBox.Text),
            Kind = kind,
            IncludeHidden = HiddenBox.IsChecked == true,
            SkipFolders = string.IsNullOrWhiteSpace(SkipFoldersBox.Text) ? null : new WildcardMask(SkipFoldersBox.Text),
            MinSize = min!.Value,
        };
        _rows.Clear();
        _groups = Array.Empty<DuplicateGroup>();
        UpdateButtons();
        _cts = new CancellationTokenSource();
        FindButton.Content = Loc.T("Stop");
        Busy.IsActive = true;
        var progress = new Progress<DuplicateProgress>(p => DispatcherQueue.TryEnqueue(() =>
        {
            if (_cts == null)
                return;
            StatusText.Text = p.Stage == "Listing"
                ? Loc.F("Listing: {0}  ·  {1} files", p.Current, p.Done.ToString("N0"))
                : Loc.F("Comparing: {0} / {1}  ·  {2}", p.Done.ToString("N0"), p.Total.ToString("N0"), p.Current);
        }));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _groups = await DuplicateFinder.FindAsync(criteria, progress, _cts.Token);
            foreach (DuplicateGroup group in _groups)
            {
                _rows.Add(new DuplicateHeaderRow(group));
                foreach (FileEntry file in group.Files)
                {
                    var row = new DuplicateFileRow(file, group);
                    row.CheckedChanged += (_, _) => UpdateButtons();
                    _rows.Add(row);
                }
            }
            long wasted = _groups.Sum(g => g.Wasted);
            StatusText.Text = _groups.Count == 0
                ? Loc.T("No duplicates found")
                : Loc.F("{0} groups, {1} files  ·  {2} could be freed", _groups.Count.ToString("N0"),
                      _groups.Sum(g => g.Files.Count).ToString("N0"), Formats.ShortSize(wasted))
                  + "  ·  " + Loc.F("{0} s", clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("Search stopped");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            FindButton.Content = Loc.T("Find");
            Busy.IsActive = false;
            UpdateButtons();
        }
    }

    private IEnumerable<DuplicateFileRow> Files => _rows.OfType<DuplicateFileRow>();

    private void UpdateButtons()
    {
        int chosen = Files.Count(r => r.IsChecked);
        SelectButton.IsEnabled = _groups.Count > 0;
        DeleteButton.IsEnabled = chosen > 0;
        ShowInPanelButton.IsEnabled = chosen > 0;
        DeleteText.Text = chosen == 0 ? Loc.T("Delete selected")
            : Loc.F("Delete {0} selected ({1})", chosen.ToString("N0"), Formats.ShortSize(Files.Where(r => r.IsChecked).Sum(r => r.Entry.Size)));
    }

    /// <summary>Selects the copies to go: in each group all but the one to keep.</summary>
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        string how = (sender as FrameworkElement)?.Tag as string ?? "None";
        if (how == "Folder")
        {
            if (Results.SelectedItem is not DuplicateFileRow selected)
            {
                StatusText.Text = Loc.T("Click a file first: its folder's copies are selected.");
                return;
            }
            string folder = selected.Folder;
            foreach (DuplicateFileRow row in Files)
            {
                // never every file of a group
                bool inFolder = string.Equals(row.Folder, folder, StringComparison.OrdinalIgnoreCase);
                bool othersOutside = Files.Any(r => r.Group == row.Group && !string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase));
                row.IsChecked = inFolder && othersOutside;
            }
            return;
        }
        foreach (IGrouping<DuplicateGroup, DuplicateFileRow> group in Files.GroupBy(r => r.Group))
        {
            DuplicateFileRow? keep = how switch
            {
                "Newest" => group.MaxBy(r => r.Entry.LastWrite),
                "Oldest" => group.MinBy(r => r.Entry.LastWrite),
                "First" => group.First(),
                _ => null,
            };
            foreach (DuplicateFileRow row in group)
                row.IsChecked = how != "None" && row != keep;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Files.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0)
            return;
        // a group never loses all of its files without a question
        bool wholeGroup = chosen.GroupBy(r => r.Group).Any(g => g.Count() == g.Key.Files.Count);
        string message = Loc.F("Do you want to move the {0} selected files to the Recycle Bin?", chosen.Count.ToString("N0"));
        if (wholeGroup)
            message += "\n\n" + Loc.T("Every copy of some files is selected: none of them would be left.");
        ContentDialog dialog = _dialogs.Create("Delete", new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = Loc.T("Delete");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = wholeGroup ? ContentDialogButton.Close : ContentDialogButton.Primary;
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            return;
        await Core.Interop.ShellOperations.DeleteAsync(WindowHandle, chosen.Select(r => r.Entry.FullPath).ToList(), permanent: false);
        // the rows of what is gone go; a group with one file left goes too
        foreach (DuplicateFileRow row in chosen.Where(r => !File.Exists(r.Entry.FullPath)))
            _rows.Remove(row);
        foreach (DuplicateHeaderRow header in _rows.OfType<DuplicateHeaderRow>().ToList())
        {
            var left = Files.Where(r => r.Group == header.Group).ToList();
            if (left.Count <= 1)
            {
                _rows.Remove(header);
                foreach (DuplicateFileRow r in left)
                    _rows.Remove(r);
            }
        }
        UpdateButtons();
        StatusText.Text = Loc.F("{0} files moved to the Recycle Bin", chosen.Count(r => !File.Exists(r.Entry.FullPath)).ToString("N0"));
        await _main.RefreshAllAsync();
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FocusButton.IsEnabled = Results.SelectedItem is DuplicateFileRow;

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await FocusSelectedAsync();

    private async void FocusButton_Click(object sender, RoutedEventArgs e) => await FocusSelectedAsync();

    private async Task FocusSelectedAsync()
    {
        if (Results.SelectedItem is DuplicateFileRow row)
            await _focusInPanel(row.Folder, row.Name);
    }

    private async void ShowInPanel_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Files.Where(r => r.IsChecked).Select(r => r.Entry).ToList();
        if (chosen.Count == 0)
            return;
        string root = SearchForm.Folders(LookInBox.Text).FirstOrDefault() ?? string.Empty;
        string path = FoundListProvider.Publish(Loc.F("Duplicates ({0})", chosen.Count.ToString("N0")), root, chosen);
        await _focusInPanel(path, null);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_cts != null)
                _cts.Cancel();
            else
                Close();
        }
        else if (e.Key == Windows.System.VirtualKey.Space && Results.SelectedItem is DuplicateFileRow row &&
                 Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) is ListViewItem)
        {
            e.Handled = true;
            row.IsChecked = !row.IsChecked; // Space checks the focused file, like Insert in the panels
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && _cts == null &&
                 Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            e.Handled = true;
            _ = RunAsync();
        }
    }
}
