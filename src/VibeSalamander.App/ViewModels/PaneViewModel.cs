// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>A drive in the drive bar.</summary>
public sealed class DriveButtonViewModel : BindableBase
{
    private ImageSource? _icon;

    public DriveButtonViewModel(DriveEntryInfo drive)
    {
        Drive = drive;
    }

    public DriveEntryInfo Drive { get; }
    public string Letter => Drive.Root[..1];

    private bool _separatorAfter;

    /// <summary>A separator follows this drive in the drive bar (Configuration → Change Drive Menu and Drive Bar).</summary>
    public bool SeparatorAfter
    {
        get => _separatorAfter;
        set
        {
            if (SetProperty(ref _separatorAfter, value))
                OnPropertyChanged(nameof(SeparatorVisibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility SeparatorVisibility => _separatorAfter ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ToolTip => Drive.HasSize
        ? Drive.Caption + "\n" + Loc.F("{0} free of {1}", Formats.ShortSize(Drive.FreeSpace), Formats.ShortSize(Drive.TotalSize))
        : Drive.Caption;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }
}

/// <summary>Arguments of <see cref="PaneViewModel.ListingChanged"/>: where the caret should be after a (re)load.</summary>
public sealed class ListingChangedEventArgs : EventArgs
{
    public ListingChangedEventArgs(int caretIndex, bool samePlace)
    {
        CaretIndex = caretIndex;
        SamePlace = samePlace;
    }

    public int CaretIndex { get; }

    /// <summary>The same directory again (a refresh, a new sort): the list keeps its scroll position.</summary>
    public bool SamePlace { get; }
}

/// <summary>State and behavior of one panel (left or right).</summary>
public sealed partial class PaneViewModel : BindableBase, IDisposable
{
    private readonly FileSystemRegistry _registry;
    private readonly DispatcherQueue _dispatcher;
    private readonly IconCache _icons;
    private readonly Func<bool> _showHidden;
    private readonly NavigationHistory _history = new();
    private readonly Dictionary<char, string> _lastPathOnDrive = new();

    private IFileSystemProvider _provider;
    private IReadOnlyList<FileItemViewModel> _items = Array.Empty<FileItemViewModel>();
    private IReadOnlyList<FileEntry> _entries = Array.Empty<FileEntry>();
    private IReadOnlyList<DriveButtonViewModel> _drives = Array.Empty<DriveButtonViewModel>();
    private string _path = string.Empty;
    private string _freeSpaceText = string.Empty;
    private string _quickSearch = string.Empty;
    private string? _error;
    private bool _isActive;
    private bool _isLoading;
    private int _caretIndex = -1;
    private SortMode _sortMode;
    private bool _sortDescending;
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _refreshTimer;
    private int _hiddenCount;
    private int _filteredCount;
    private string _filter = string.Empty;
    private WildcardMask? _filterMask;
    private List<string> _rememberedSelection = new();

    public PaneViewModel(string name, FileSystemRegistry registry, DispatcherQueue dispatcher, IconCache icons,
                         PanelSettings settings, Func<bool> showHidden, Func<AppSettings> appSettings)
    {
        _appSettings = appSettings;
        Name = name;
        _registry = registry;
        _dispatcher = dispatcher;
        _icons = icons;
        _showHidden = showHidden;
        _sortMode = settings.SortMode;
        _sortDescending = settings.SortDescending;
        _viewMode = Enum.IsDefined(settings.ViewMode) ? settings.ViewMode : ViewMode.Detailed;
        _defaultView = _viewMode;
        settings.Columns ??= new ColumnSettings();
        Columns = new ColumnLayout(settings.Columns);
        _provider = registry.Find(ThisPcProvider.RootPath)!;
    }

    // ---- view ----------------------------------------------------------------------------------

    private readonly Func<AppSettings> _appSettings;
    private ViewMode _viewMode;

    /// <summary>
    /// The panel's own view: in the Classic views the view of every folder, in the Modern views the view of folders
    /// that have none of their own (and aren't picture folders). Kept in the settings.
    /// </summary>
    private ViewMode _defaultView;

    private bool IsModernViews => _appSettings().ViewStyle == ViewStyle.Modern;

    /// <summary>
    /// Brief / Detailed / Icons / Thumbnails / Tiles (Alt+1…5): the view chosen by the user. Modern views remember it
    /// for the folder; Classic views use it everywhere.
    /// </summary>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (IsModernViews)
                FolderViews.Remember(_appSettings().FolderViews, IsThisPc ? ThisPcProvider.RootPath : _path, value);
            else
                _defaultView = value;
            ShowView(value);
        }
    }

    /// <summary>The panel's view of Configuration → Views (see <see cref="_defaultView"/>).</summary>
    public ViewMode DefaultView
    {
        get => _defaultView;
        set
        {
            _defaultView = value;
            ApplyViewStyle();
        }
    }

    private void ShowView(ViewMode view)
    {
        if (SetProperty(ref _viewMode, view, nameof(ViewMode)))
            OnPropertyChanged(nameof(EffectiveViewMode));
    }

    /// <summary>The view this folder gets: Classic = the panel's view; Modern = its remembered view, else by its files.</summary>
    private ViewMode ViewForHere()
    {
        if (!IsModernViews)
            return _defaultView;
        if (FolderViews.Recall(_appSettings().FolderViews, IsThisPc ? ThisPcProvider.RootPath : _path) is ViewMode remembered)
            return remembered;
        if (IsThisPc)
            return ViewMode.Tiles; // like Explorer's This PC
        // pictures and videos as thumbnails, like Explorer (not inside archives or on servers: they'd have to be unpacked or downloaded)
        if (_provider.IsLocalDisk && FolderViews.IsPictureFolder(_entries))
            return ViewMode.Thumbnails;
        return _defaultView;
    }

    /// <summary>Classic or Modern views were switched (Configuration → Views): the panel shows the view it now gets here.</summary>
    public void ApplyViewStyle() => ShowView(ViewForHere());

    /// <summary>The view actually shown (This PC too: Detailed is its drive list with capacity bars).</summary>
    public ViewMode EffectiveViewMode => _viewMode;

    /// <summary>Detailed view column widths/visibility of this panel.</summary>
    public ColumnLayout Columns { get; }

    // ---- directory sizes -------------------------------------------------------------------------

    /// <summary>Calculated directory sizes of the current directory (kept across refreshes, dropped when leaving it).</summary>
    private readonly Dictionary<string, long> _directorySizes = new(StringComparer.OrdinalIgnoreCase);
    private int _busyCount;

    /// <summary>Loading or calculating (the directory line shows a progress ring).</summary>
    public bool IsBusy => _isLoading || _busyCount > 0;

    /// <summary>
    /// Calculates the size of each directory in <paramref name="items"/> (shown in the Size column) and returns
    /// the total occupied space of the items, files included.
    /// </summary>
    public async Task<OccupiedSpace> CalculateSizesAsync(IReadOnlyList<FileItemViewModel> items, CancellationToken ct = default)
    {
        _busyCount++;
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            var total = new OccupiedSpace(0, 0, 0);
            foreach (FileItemViewModel item in items)
            {
                if (item.Entry.IsUpDir || item.IsDrive)
                    continue;
                if (!item.Entry.IsDirectory)
                {
                    total += new OccupiedSpace(item.Entry.Size, 1, 0);
                    continue;
                }
                OccupiedSpace space = await DirectorySize.CalculateAsync(item.Entry.FullPath, ct);
                _directorySizes[item.Entry.FullPath] = space.Bytes;
                item.DirectorySize = space.Bytes;
                // an automatic refresh during the calculation rebuilt the rows: update the new one too
                foreach (FileItemViewModel current in _items)
                    if (current != item && string.Equals(current.Entry.FullPath, item.Entry.FullPath, StringComparison.OrdinalIgnoreCase))
                        current.DirectorySize = space.Bytes;
                total += space + new OccupiedSpace(0, 0, 1);
            }
            OnPropertyChanged(nameof(InfoText));
            return total;
        }
        finally
        {
            _busyCount--;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>"Left" or "Right".</summary>
    public string Name { get; }

    /// <summary>Raised after a listing is loaded; the view applies the caret and scrolls to it.</summary>
    public event EventHandler<ListingChangedEventArgs>? ListingChanged;

    public IReadOnlyList<FileItemViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>
    /// What the list shows: the items, on This PC in groups like Explorer (Devices and drives, Network
    /// locations). The groups keep the items' order, so an index means the same in both.
    /// </summary>
    public object ItemsSource => _groupedItems ?? _items;

    private object? _groupedItems;

    /// <summary>This PC's groups: 0 = devices and drives, 1 = network locations.</summary>
    private static int DriveGroupOf(FileItemViewModel item) => item.Drive?.Type == DriveType.Network ? 1 : 0;

    private static object GroupDrives(IReadOnlyList<FileItemViewModel> items)
    {
        var groups = new List<ItemGroup>
        {
            new(Loc.T("Devices and drives"), items.Where(i => DriveGroupOf(i) == 0)),
            new(Loc.T("Network locations"), items.Where(i => DriveGroupOf(i) == 1)),
        };
        return new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups.Where(g => g.Count > 0).ToList() }.View;
    }

    public IReadOnlyList<DriveButtonViewModel> Drives
    {
        get => _drives;
        private set => SetProperty(ref _drives, value);
    }

    public string Path
    {
        get => _path;
        private set
        {
            if (SetProperty(ref _path, value))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(IsThisPc));
                OnPropertyChanged(nameof(IsLocalDisk));
                OnPropertyChanged(nameof(IsArchive));
                OnPropertyChanged(nameof(IsRemote));
                OnPropertyChanged(nameof(EffectiveViewMode));
                OnPropertyChanged(nameof(DriveMenuGlyph));
                _directorySizes.Clear(); // sizes belong to the directory we left
                _hiddenNames.Clear();
            }
        }
    }

    public string DisplayPath => _provider.GetDisplayPath(_path);

    /// <summary>Icon of the drive drop-down: This PC, or the kind of drive the panel is on.</summary>
    public string DriveMenuGlyph => IsThisPc ? "\uE7F4" : CurrentDriveType switch
    {
        DriveType.Removable => "\uE88E",
        DriveType.CDRom => "\uE958",
        DriveType.Network => "\uE8CE",
        _ => "\uEDA2",
    };
    public bool IsThisPc => _provider is ThisPcProvider;
    public bool IsLocalDisk => _provider.IsLocalDisk;

    /// <summary>Find's results shown in the panel (Show in panel): not a folder, but the items are files on disk.</summary>
    public bool IsFoundList => _provider is FoundListProvider;

    /// <summary>The items are on disk: copy, move and delete work on them.</summary>
    public bool HasDiskItems => _provider.IsLocalDisk || _provider is FoundListProvider;

    /// <summary>The panel shows the inside of an archive (read-only: copy out with F5, open files with Enter).</summary>
    public bool IsArchive => _provider is ArchiveFileSystemProvider;

    /// <summary>
    /// The panel is about to leave the server it shows (pane, the server path); false keeps it there.
    /// Set by the main view model (it asks whether to disconnect).
    /// </summary>
    public Func<PaneViewModel, string, Task<bool>>? LeavingServer { get; set; }

    /// <summary>A plugin file system (FTP, SFTP, ...): its files aren't on this computer.</summary>
    public bool IsRemote => _provider is RemoteFileSystemProvider;

    /// <summary>Which archive and which directory in it, when <see cref="IsArchive"/>.</summary>
    public ArchiveLocation? Archive => _provider is ArchiveFileSystemProvider archives ? archives.Locate(_path) : null;

    /// <summary>Enter on a file inside an archive: the main view model unpacks it to a temporary folder and opens it.</summary>
    public event EventHandler<FileItemViewModel>? ArchiveFileOpenRequested;

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set => SetProperty(ref _freeSpaceText, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(InfoText));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    public SortMode SortMode
    {
        get => _sortMode;
        private set => SetProperty(ref _sortMode, value);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    /// <summary>Text typed for quick search (shown in the directory line).</summary>
    public string QuickSearch
    {
        get => _quickSearch;
        private set => SetProperty(ref _quickSearch, value);
    }

    /// <summary>Panel filter mask (Ctrl+F12); empty = no filter. Applies to files only, like Salamander.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _filter, value))
            {
                _filterMask = value.Length == 0 ? null : new WildcardMask(value);
                OnPropertyChanged(nameof(FilterText));
                Rebuild();
            }
        }
    }

    /// <summary>Indicator text for the directory line ("Filter: *.txt").</summary>
    public string FilterText => _filter.Length == 0 ? string.Empty : Loc.F("Filter: {0}", _filter);

    /// <summary>Index of the focused item (the ListView's selected index).</summary>
    public int CaretIndex
    {
        get => _caretIndex;
        set
        {
            if (SetProperty(ref _caretIndex, value))
                OnPropertyChanged(nameof(InfoText));
        }
    }

    public FileItemViewModel? FocusedItem => _caretIndex >= 0 && _caretIndex < _items.Count ? _items[_caretIndex] : null;

    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;

    // ---- information line --------------------------------------------------------------------

    public string InfoText
    {
        get
        {
            if (_error != null)
                return _error;

            var marked = _items.Where(i => i.IsMarked).ToList();
            if (marked.Count > 0)
            {
                int dirs = marked.Count(i => i.Entry.IsDirectory);
                int files = marked.Count - dirs;
                long bytes = marked.Sum(i => i.Entry.IsDirectory ? i.DirectorySize ?? 0 : i.Entry.Size); // calculated directory sizes count too
                string format = (files == 1, dirs == 1) switch
                {
                    (true, true) => "Selected: {0} bytes in {1} file and {2} directory",
                    (true, false) => "Selected: {0} bytes in {1} file and {2} directories",
                    (false, true) => "Selected: {0} bytes in {1} files and {2} directory",
                    _ => "Selected: {0} bytes in {1} files and {2} directories",
                };
                return Loc.F(format, Formats.ExactSize(bytes), files, dirs);
            }

            FileItemViewModel? item = FocusedItem;
            if (item == null)
                return _hiddenCount + _filteredCount > 0 ? Loc.F("No visible items ({0} hidden or filtered)", _hiddenCount + _filteredCount) : Loc.T("No items");
            if (item.Drive is { } d)
            {
                return d.HasSize
                    ? Loc.F("{0}: {1} free of {2}, {3}", d.Caption, Formats.ShortSize(d.FreeSpace), Formats.ShortSize(d.TotalSize), d.FileSystem)
                    : Loc.F("{0}: not ready", d.Caption);
            }
            FileEntry e = item.Entry;
            if (e.IsUpDir)
                return Loc.T(".. (parent directory)");
            return FormatInfoLine(item);
        }
    }

    /// <summary>The information line for an item, from Configuration → Appearance → Content of Information Line.</summary>
    private static string FormatInfoLine(FileItemViewModel item)
    {
        FileEntry e = item.Entry;
        string size = e.IsDirectory
            ? (item.DirectorySize is long d ? Formats.Size(d, UiState.SizeFormat) : "DIR")
            : Formats.Size(e.Size, UiState.SizeFormat);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = item.FullName,
            ["NamePart"] = item.Name,
            ["Ext"] = item.Ext,
            ["Size"] = size,
            ["Date"] = Formats.Date(e.LastWrite),
            ["Time"] = Formats.Time(e.LastWrite),
            ["Attributes"] = Formats.Attributes(e.Attributes),
            ["Type"] = item.TileType,
            ["Path"] = System.IO.Path.GetDirectoryName(e.FullPath) ?? string.Empty,
            ["FullName"] = e.FullPath,
        };
        string format = UiState.InfoLineFormat;
        var sb = new System.Text.StringBuilder(format.Length + 32);
        int i = 0;
        while (i < format.Length)
        {
            int start = format.IndexOf("$(", i, StringComparison.Ordinal);
            int end = start < 0 ? -1 : format.IndexOf(')', start + 2);
            if (start < 0 || end < 0)
            {
                sb.Append(format, i, format.Length - i);
                break;
            }
            sb.Append(format, i, start - i);
            string key = format[(start + 2)..end];
            sb.Append(values.TryGetValue(key, out string? value) ? value : format[start..(end + 1)]);
            i = end + 1;
        }
        return sb.ToString();
    }

}

/// <summary>A group of items with its title (This PC: Devices and drives, Network locations).</summary>
public sealed class ItemGroup : List<FileItemViewModel>
{
    public ItemGroup(string title, IEnumerable<FileItemViewModel> items) : base(items) => Title = title;

    public string Title { get; }
}
