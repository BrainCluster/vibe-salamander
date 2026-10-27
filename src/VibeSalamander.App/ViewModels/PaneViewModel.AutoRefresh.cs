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

// PaneViewModel: refreshing by itself when the directory changes.
public sealed partial class PaneViewModel
{
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
