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
public sealed class PaneViewModel : BindableBase, IDisposable
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
                         PanelSettings settings, Func<bool> showHidden)
    {
        Name = name;
        _registry = registry;
        _dispatcher = dispatcher;
        _icons = icons;
        _showHidden = showHidden;
        _sortMode = settings.SortMode;
        _sortDescending = settings.SortDescending;
        _viewMode = Enum.IsDefined(settings.ViewMode) ? settings.ViewMode : ViewMode.Detailed;
        settings.Columns ??= new ColumnSettings();
        Columns = new ColumnLayout(settings.Columns);
        _provider = registry.Find(ThisPcProvider.RootPath)!;
    }

    // ---- view ----------------------------------------------------------------------------------

    private ViewMode _viewMode;

    /// <summary>Brief / Detailed / Icons / Thumbnails / Tiles (Alt+1…5).</summary>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
                OnPropertyChanged(nameof(EffectiveViewMode));
        }
    }

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

    // ---- navigation --------------------------------------------------------------------------

    public async Task NavigateAsync(string path, string? focusName = null, bool addToHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        IFileSystemProvider? provider = _registry.Find(path);
        if (provider == null)
        {
            Error = Loc.F("Unknown path: {0}", path);
            return;
        }
        if (provider is LocalFileSystemProvider)
            path = PathUtil.NormalizeDirectory(path);
        // leaving a server (FTP, SFTP): asked whether to disconnect; no = the panel stays
        if (_provider is RemoteFileSystemProvider && LeavingServer != null &&
            !RemoteFileSystemProvider.SameServer(_path, path) && !await LeavingServer(this, _path))
            return;

        string previous = _path;
        bool ok = await LoadAsync(provider, path, focusName, keepCaret: false);
        if (ok && addToHistory && previous.Length > 0 && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
        {
            _history.Push(previous);
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
    }

    private bool _isEditing;
    private bool _refreshPending;

    /// <summary>
    /// True while a name is edited in place: refreshes (auto refresh, finished transfers) are held back
    /// until the edit ends, because rebuilding the rows would end the edit.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            if (!value && _refreshPending)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
        }
    }

    public Task RefreshAsync()
    {
        if (_isEditing)
        {
            _refreshPending = true;
            return Task.CompletedTask;
        }
        if (IsLocalDisk && _path.Length > 0 && !Directory.Exists(_path))
            return GoToAccessibleAsync(); // the directory was deleted or the drive removed
        return LoadAsync(_provider, _path, FocusedItem?.Entry.Name, keepCaret: true);
    }

    /// <summary>
    /// The directory is gone: go to the nearest existing parent, or to the configured fallback
    /// (Configuration → Drives → "If path in panel is inaccessible, go to"), or This PC.
    /// </summary>
    private async Task GoToAccessibleAsync()
    {
        string? fallback = UiState.Drives?.InaccessibleFallback;
        string? parent = _path;
        while (!string.IsNullOrEmpty(parent))
        {
            parent = System.IO.Path.GetDirectoryName(parent.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent))
                break;
            if (Directory.Exists(parent))
            {
                // a parent on the same drive is only used when no fallback is configured
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    await NavigateAsync(parent, addToHistory: false);
                    return;
                }
                break;
            }
        }
        if (!string.IsNullOrWhiteSpace(fallback) && (Directory.Exists(fallback) || fallback.StartsWith(ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase)))
            await NavigateAsync(fallback, addToHistory: false);
        else
            await NavigateAsync(ThisPcProvider.RootPath, addToHistory: false);
    }

    public async Task GoUpAsync()
    {
        ParentLocation? parent = _provider.GetParent(_path);
        if (parent != null)
            await NavigateAsync(parent.Path, parent.FocusName);
    }

    public async Task GoToRootAsync()
    {
        if (!IsLocalDisk)
            return;
        string? root = PathUtil.GetRoot(_path);
        if (root != null)
            await NavigateAsync(root);
    }

    public async Task GoBackAsync()
    {
        string? target = _history.GoBack(_path);
        if (target != null)
            await NavigateAsync(target, addToHistory: false);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public async Task GoForwardAsync()
    {
        string? target = _history.GoForward(_path);
        if (target != null)
            await NavigateAsync(target, addToHistory: false);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>Switches to a drive, returning to the last directory visited on it (like Salamander).</summary>
    public async Task ChangeDriveAsync(string root)
    {
        char letter = char.ToUpperInvariant(root[0]);
        string target = _lastPathOnDrive.TryGetValue(letter, out string? last) && Directory.Exists(last) ? last : root;
        await NavigateAsync(target);
    }

    /// <summary>Enter on the focused item: open directory/drive, run/open file.</summary>
    public async Task OpenFocusedAsync()
    {
        FileItemViewModel? item = FocusedItem;
        if (item == null)
            return;
        FileEntry e = item.Entry;
        if (e.IsUpDir)
            await GoUpAsync();
        else if (item.Drive is { } drive)
            await ChangeDriveAsync(drive.Root);
        else if (e.IsDirectory)
            await NavigateAsync(e.FullPath);
        else if (IsArchive || IsRemote)
            ArchiveFileOpenRequested?.Invoke(this, item); // a file in an archive or on a server: a temporary copy is opened
        else if (_registry.Find(e.FullPath) is ArchiveFileSystemProvider)
            await NavigateAsync(e.FullPath); // into the archive, like a directory
        else if (DiskImageOpenRequested != null && Core.Interop.DiskImages.IsDiskImage(e.FullPath))
        {
            DiskImageOpenRequested(this, e.FullPath); // mounted (or opened, by the configuration) by the main view model
            FileOpened?.Invoke(this, e.FullPath);
        }
        else
        {
            Core.Interop.ShellOperations.Open(e.FullPath, _path);
            FileOpened?.Invoke(this, e.FullPath);
        }
    }

    private async Task<bool> LoadAsync(IFileSystemProvider provider, string path, string? focusName, bool keepCaret)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        HashSet<string>? marked = keepCaret
            ? new HashSet<string>(_items.Where(i => i.IsMarked).Select(i => i.Entry.Name), StringComparer.OrdinalIgnoreCase)
            : null;
        int previousCaret = _caretIndex;

        DirectoryListing listing;
        try
        {
            listing = await provider.ListAsync(path, new ListingOptions { ShowHidden = _showHidden() }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (_loadCts == cts)
                IsLoading = false;
        }
        if (cts.IsCancellationRequested)
            return false;

        // A missing/inaccessible directory: keep the current listing, show the error.
        if (listing.Error != null && listing.Entries.Count <= 1 && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && _path.Length > 0)
        {
            Error = $"{path}: {Loc.T(listing.Error)}";
            return false;
        }

        ApplyListing(provider, listing, focusName, marked, keepCaret ? previousCaret : 0, navigated: !keepCaret);
        UpdateWatcher();
        return true;
    }

    /// <summary>Shows a listing: path, entries, free space, then the sorted rows with the caret.</summary>
    private void ApplyListing(IFileSystemProvider provider, DirectoryListing listing, string? focusName, HashSet<string>? marked,
        int fallbackCaret, bool navigated)
    {
        // leaving a folder with thousands of files: its rows are garbage now, give the memory back soon
        if (_items.Count >= 3000 && !string.Equals(listing.Path, _path, StringComparison.OrdinalIgnoreCase))
            MemoryCleanup.Schedule();
        _provider = provider;
        Path = listing.Path;
        OnPropertyChanged(nameof(DisplayPath));
        _entries = listing.Entries;
        _hiddenCount = listing.HiddenCount;
        FreeSpaceText = listing.FreeSpace is long free ? Loc.F("{0} free", Formats.ShortSize(free)) : string.Empty;
        Error = listing.Error == null ? null : Loc.T(listing.Error);
        if (navigated)
            QuickSearch = string.Empty;
        if (provider.IsLocalDisk && listing.Path.Length >= 2 && listing.Path[1] == ':')
            _lastPathOnDrive[char.ToUpperInvariant(listing.Path[0])] = listing.Path;

        ApplySort(focusName, marked, fallbackCaret);
        if (navigated)
            Navigated?.Invoke(this, listing.Path);
    }

    /// <summary>Raised after the panel went to another directory (for the list of working directories, Alt+F12).</summary>
    public event EventHandler<string>? Navigated;

    /// <summary>Raised when a file is opened from the panel (for the list of opened files, Alt+F11).</summary>
    public event EventHandler<string>? FileOpened;

    /// <summary>Enter on a disk image (.iso, .img, .vhd, .vhdx): the main view model mounts it and shows the drive.</summary>
    public event EventHandler<string>? DiskImageOpenRequested;

    // ---- hidden names (Edit → Hide Selected / Unselected Names, Show Hidden Names) ---------------

    private readonly HashSet<string> _hiddenNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hides the marked (or the unmarked) names from the panel until Show Hidden Names or another directory.</summary>
    public int HideNames(bool selected)
    {
        var names = _items.Where(i => !i.Entry.IsUpDir && !i.IsDrive && i.IsMarked == selected).Select(i => i.Entry.Name).ToList();
        if (selected && names.Count == 0 && FocusedItem is { Entry.IsUpDir: false, IsDrive: false } focused)
            names.Add(focused.Entry.Name); // nothing marked: hide the focused item
        foreach (string name in names)
            _hiddenNames.Add(name);
        if (names.Count > 0)
            Rebuild();
        return names.Count;
    }

    public int ShowHiddenNames()
    {
        int count = _hiddenNames.Count;
        _hiddenNames.Clear();
        if (count > 0)
            Rebuild();
        return count;
    }

    /// <summary>Marks the items whose names are in <paramref name="names"/>; returns how many were found.</summary>
    public int MarkNames(IReadOnlyCollection<string> names)
    {
        RememberSelection(); // the previous marks come back with Restore Selection (Ctrl+W)
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int found = 0;
        foreach (FileItemViewModel item in _items)
        {
            if (item.Entry.IsUpDir || item.IsDrive)
                continue;
            bool mark = set.Contains(item.Entry.Name);
            item.IsMarked = mark;
            if (mark)
                found++;
        }
        OnPropertyChanged(nameof(InfoText));
        return found;
    }

    /// <summary>Rebuilds the item list from the current entries (after sorting, density or theme changes).</summary>
    public void Rebuild() => ApplySort(FocusedItem?.Entry.Name,
        new HashSet<string>(_items.Where(i => i.IsMarked).Select(i => i.Entry.Name), StringComparer.OrdinalIgnoreCase), _caretIndex);

    private void ApplySort(string? focusName, HashSet<string>? marked, int fallbackCaret)
    {
        IEnumerable<FileEntry> visible = _entries;
        _filteredCount = 0;
        if (_filterMask != null)
        {
            visible = _entries.Where(e => e.IsDirectory || _filterMask.IsMatch(e.Name)).ToList();
            _filteredCount = _entries.Count - ((List<FileEntry>)visible).Count;
        }
        if (_hiddenNames.Count > 0)
        {
            var shown = visible.Where(e => e.IsUpDir || !_hiddenNames.Contains(e.Name)).ToList();
            _filteredCount += visible.Count() - shown.Count;
            visible = shown;
        }
        List<FileEntry> sorted = new EntryComparer(_sortMode, _sortDescending, UiState.SortOptions).Sort(visible);
        var items = new List<FileItemViewModel>(sorted.Count);
        foreach (FileEntry entry in sorted)
        {
            var vm = new FileItemViewModel(entry, Columns);
            if (marked != null && !entry.IsUpDir && marked.Contains(entry.Name))
                vm.IsMarked = true;
            if (entry.IsDirectory && _directorySizes.Count > 0 && _directorySizes.TryGetValue(entry.FullPath, out long size))
                vm.DirectorySize = size;
            items.Add(vm);
        }

        int caret = -1;
        if (!string.IsNullOrEmpty(focusName))
            caret = items.FindIndex(i => string.Equals(i.Entry.Name, focusName, StringComparison.OrdinalIgnoreCase));
        if (caret < 0)
            caret = items.Count == 0 ? -1 : Math.Clamp(fallbackCaret, 0, items.Count - 1);

        bool samePlace = string.Equals(_lastListedPath, _path, StringComparison.OrdinalIgnoreCase);
        _lastListedPath = _path;
        Items = items;
        _caretIndex = caret;
        OnPropertyChanged(nameof(CaretIndex));
        OnPropertyChanged(nameof(InfoText));
        ListingChanged?.Invoke(this, new ListingChangedEventArgs(caret, samePlace));
    }

    /// <summary>The directory of the last list shown (to tell a refresh from going elsewhere).</summary>
    private string? _lastListedPath;

    public void SetSort(SortMode mode)
    {
        if (_sortMode == mode)
            SortDescending = !_sortDescending;
        else
        {
            SortMode = mode;
            SortDescending = false;
        }
        Rebuild();
    }

    public void StoreSettings(PanelSettings settings)
    {
        settings.Path = IsThisPc ? ThisPcProvider.RootPath : _path;
        settings.SortMode = _sortMode;
        settings.SortDescending = _sortDescending;
        settings.ViewMode = _viewMode;
    }

    // ---- drives ------------------------------------------------------------------------------

    public async Task LoadDrivesAsync()
    {
        IReadOnlyList<DriveEntryInfo> drives = await ThisPcProvider.QueryDrivesAsync();
        var buttons = drives.Select(d => new DriveButtonViewModel(d)).ToList();
        foreach (var b in buttons)
        {
            var entry = new FileEntry(b.Drive.Root, b.Drive.Root, true, 0, DateTime.MinValue, FileAttributes.Directory, tag: b.Drive);
            _icons.Request(entry, icon => b.Icon = icon);
        }
        Drives = buttons;
        OnPropertyChanged(nameof(VisibleDrives));
    }

    /// <summary>Drives for the drive bar and the Change Drive menu (Configuration → Change Drive Menu and Drive Bar).</summary>
    public IReadOnlyList<DriveButtonViewModel> VisibleDrives
    {
        get
        {
            DriveOptions? options = UiState.Drives;
            if (options == null)
                return _drives;
            foreach (DriveButtonViewModel d in _drives)
                d.SeparatorAfter = options.SeparatorAfter.Contains(d.Letter, StringComparer.OrdinalIgnoreCase);
            return _drives.Where(d => !options.HiddenDrives.Contains(d.Letter, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>The drive options changed: the drive bar is rebuilt.</summary>
    public void RefreshVisibleDrives() => OnPropertyChanged(nameof(VisibleDrives));

    // ---- marks (Salamander selection) ----------------------------------------------------------

    public IReadOnlyList<FileItemViewModel> MarkedItems => _items.Where(i => i.IsMarked).ToList();

    /// <summary>Items an operation applies to: the marked items, or the focused item when nothing is marked.</summary>
    public IReadOnlyList<FileItemViewModel> GetOperationItems()
    {
        var marked = _items.Where(i => i.IsMarked).ToList();
        if (marked.Count > 0)
            return marked;
        FileItemViewModel? focused = FocusedItem;
        return focused != null && !focused.Entry.IsUpDir && !focused.IsDrive ? new[] { focused } : Array.Empty<FileItemViewModel>();
    }

    public void ToggleMark(int index)
    {
        if (index < 0 || index >= _items.Count || _items[index].Entry.IsUpDir || _items[index].IsDrive)
            return;
        _items[index].IsMarked = !_items[index].IsMarked;
        OnPropertyChanged(nameof(InfoText));
    }

    /// <summary>Marks or unmarks the items from <paramref name="from"/> to <paramref name="to"/> (inclusive, any order).</summary>
    public void MarkRange(int from, int to, bool mark)
    {
        if (_items.Count == 0)
            return;
        int lo = Math.Clamp(Math.Min(from, to), 0, _items.Count - 1);
        int hi = Math.Clamp(Math.Max(from, to), 0, _items.Count - 1);
        for (int i = lo; i <= hi; i++)
            if (!_items[i].Entry.IsUpDir && !_items[i].IsDrive)
                _items[i].IsMarked = mark;
        OnPropertyChanged(nameof(InfoText));
    }

    /// <summary>Sets the mark of one item (".." and drives can't be marked); call NotifyMarksChanged after a batch.</summary>
    public void SetMark(int index, bool mark)
    {
        if (index >= 0 && index < _items.Count && !_items[index].Entry.IsUpDir && !_items[index].IsDrive)
            _items[index].IsMarked = mark;
    }

    /// <summary>Updates the information line after SetMark calls.</summary>
    public void NotifyMarksChanged() => OnPropertyChanged(nameof(InfoText));

    /// <summary>Remembers the marked names for Restore Selection (Ctrl+W).</summary>
    public void RememberSelection()
    {
        var names = _items.Where(i => i.IsMarked).Select(i => i.Entry.Name).ToList();
        if (names.Count > 0)
            _rememberedSelection = names;
    }

    public void RestoreSelection()
    {
        var names = new HashSet<string>(_rememberedSelection, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
            if (!item.Entry.IsUpDir && !item.IsDrive && names.Contains(item.Entry.Name))
                item.IsMarked = true;
        OnPropertyChanged(nameof(InfoText));
    }

    /// <summary>Select/Unselect by mask (Num + / Num -).</summary>
    public int MarkByMask(string mask, bool includeDirectories, bool mark)
    {
        var m = new WildcardMask(mask);
        int count = 0;
        foreach (var item in _items)
        {
            if (item.Entry.IsUpDir || item.IsDrive || (item.Entry.IsDirectory && !includeDirectories))
                continue;
            if (m.IsMatch(item.Entry.Name))
            {
                item.IsMarked = mark;
                count++;
            }
        }
        OnPropertyChanged(nameof(InfoText));
        return count;
    }

    /// <summary>(Un)marks all files with the focused file's extension (Shift+Num ±) or base name (Ctrl+Shift+Num ±).</summary>
    public void MarkSame(bool byExtension, bool mark)
    {
        FileItemViewModel? focused = FocusedItem;
        if (focused == null || focused.Entry.IsUpDir || focused.IsDrive)
            return;
        string key = byExtension ? focused.Entry.Extension : focused.Entry.BaseName;
        foreach (var item in _items)
        {
            if (item.Entry.IsUpDir || item.IsDrive || item.Entry.IsDirectory != focused.Entry.IsDirectory)
                continue;
            string value = byExtension ? item.Entry.Extension : item.Entry.BaseName;
            if (string.Equals(value, key, StringComparison.OrdinalIgnoreCase))
                item.IsMarked = mark;
        }
        OnPropertyChanged(nameof(InfoText));
    }

    /// <summary>Index of the previous/next marked item from the caret (Alt+Up / Alt+Down), or -1.</summary>
    public int FindMarked(int direction)
    {
        for (int i = _caretIndex + direction; i >= 0 && i < _items.Count; i += direction)
            if (_items[i].IsMarked)
                return i;
        return -1;
    }

    public void MarkAll(bool mark)
    {
        if (!mark)
            RememberSelection();
        foreach (var item in _items)
            if (!item.Entry.IsUpDir && !item.IsDrive)
                item.IsMarked = mark;
        OnPropertyChanged(nameof(InfoText));
    }

    public void InvertMarks()
    {
        foreach (var item in _items)
            if (!item.Entry.IsUpDir && !item.IsDrive && (UiState.SelectIncludesDirectories || !item.Entry.IsDirectory)) // files only, unless Configuration → Panels includes directories
                item.IsMarked = !item.IsMarked;
        OnPropertyChanged(nameof(InfoText));
    }

    // ---- quick search --------------------------------------------------------------------------

    /// <summary>
    /// Adds a typed character to the quick search and moves the caret; returns the new caret or -1.
    /// The same letter again ("a", "a", ...) goes through the items starting with it, like Explorer,
    /// unless a name starts with the repeated letters ("aa").
    /// </summary>
    public int QuickSearchType(char c)
    {
        string text = _quickSearch + c;
        int index = FindByPrefix(text, 0);
        if (index >= 0)
        {
            QuickSearch = text;
            return index;
        }
        if (IsRepeatedLetter(text))
        {
            // the next item with that first letter after the caret, from the top again after the last one
            string first = c.ToString();
            int next = FindByPrefix(first, _caretIndex + 1);
            if (next < 0)
                return -1;
            QuickSearch = first;
            return next;
        }
        return -1; // no match: ignore the character (Salamander beeps)
    }

    private static bool IsRepeatedLetter(string text)
    {
        if (text.Length < 2)
            return false;
        char first = char.ToUpperInvariant(text[0]);
        foreach (char ch in text)
            if (char.ToUpperInvariant(ch) != first)
                return false;
        return true;
    }

    public int QuickSearchBackspace()
    {
        if (_quickSearch.Length == 0)
            return -1;
        QuickSearch = _quickSearch[..^1];
        return _quickSearch.Length == 0 ? _caretIndex : FindByPrefix(_quickSearch, 0);
    }

    public void EndQuickSearch() => QuickSearch = string.Empty;

    private int FindByPrefix(string prefix, int start)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            int idx = (Math.Max(0, start) + i) % _items.Count;
            if (_items[idx].Entry.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                return idx;
        }
        return -1;
    }

    // ---- auto refresh --------------------------------------------------------------------------

    /// <summary>The drive type of the current directory (UNC paths count as network).</summary>
    public DriveType CurrentDriveType
    {
        get
        {
            if (!IsLocalDisk || _path.Length < 2)
                return DriveType.Unknown;
            if (_path.StartsWith(@"\\", StringComparison.Ordinal))
                return DriveType.Network;
            try
            {
                return new DriveInfo(_path[..1]).DriveType;
            }
            catch (ArgumentException)
            {
                return DriveType.Unknown;
            }
        }
    }

    /// <summary>Configuration → Drives settings for the current drive type.</summary>
    public DriveTypeOptions? CurrentDriveOptions => UiState.Drives is not { } d ? null : CurrentDriveType switch
    {
        DriveType.Removable => d.Removable,
        DriveType.CDRom => d.CdRom,
        DriveType.Network => d.Network,
        DriveType.Fixed or DriveType.Ram => d.Fixed,
        _ => null,
    };

    /// <summary>True when the panel refreshes by itself (a watcher is running).</summary>
    public bool HasAutoRefresh => _watcher != null;

    /// <summary>Applies changed drive options (auto refresh on/off) to the running watcher.</summary>
    public void ApplyDriveOptions()
    {
        _watcherGeneration++;
        _watcher?.Dispose();
        _watcher = null;
        UpdateWatcher();
    }

    private int _watcherGeneration;

    /// <summary>
    /// Watches the panel's directory for changes. The watcher is started off the UI thread (opening the
    /// directory handle can take a moment, mostly on network drives), so navigation never waits for it.
    /// </summary>
    private void UpdateWatcher()
    {
        bool autoRefresh = CurrentDriveOptions?.AutoRefresh ?? true;
        string? watchPath = _provider.IsLocalDisk && autoRefresh ? _path : null;
        if (_watcher != null && watchPath != null && string.Equals(_watcher.Path, watchPath, StringComparison.OrdinalIgnoreCase))
            return;

        int generation = ++_watcherGeneration;
        _watcher?.Dispose();
        _watcher = null;
        if (watchPath == null)
            return;
        _ = Task.Run(() =>
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(watchPath)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                                   NotifyFilters.LastWrite | NotifyFilters.Attributes,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Changed += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemChanged;
                watcher.Error += (_, _) => ScheduleRefresh();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                watcher?.Dispose();
                return; // e.g. some network shares: no auto refresh
            }
            FileSystemWatcher started = watcher;
            if (!_dispatcher.TryEnqueue(() =>
                {
                    if (generation == _watcherGeneration && _watcher == null)
                        _watcher = started;
                    else
                        started.Dispose(); // the panel moved on meanwhile
                }))
                started.Dispose();
        });
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    /// <summary>Coalesces bursts of change notifications into one refresh.</summary>
    private void ScheduleRefresh()
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = _dispatcher.CreateTimer();
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(300);
                _refreshTimer.IsRepeating = false;
                _refreshTimer.Tick += async (_, _) => await RefreshAsync();
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _watcherGeneration++; // a watcher still starting is dropped
        _watcher?.Dispose();
        _watcher = null;
        _refreshTimer?.Stop();
    }
}
