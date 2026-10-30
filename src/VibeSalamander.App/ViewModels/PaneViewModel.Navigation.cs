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

// PaneViewModel: going places (directories, drives, archives, servers), history, and showing a listing.
public sealed partial class PaneViewModel
{
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
            Services.FileLauncher.Open(e.FullPath, _path);
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
        // another folder: its view (Modern views), before its rows are made so they get the new view's templates
        if (!string.Equals(listing.Path, _lastListedPath, StringComparison.OrdinalIgnoreCase))
            ShowView(ViewForHere());
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
}
