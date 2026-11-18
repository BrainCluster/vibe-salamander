// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using VibeSalamander.Core;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.ViewModels;

// PaneViewModel: tabs. A side has one panel; its tabs are saved states of it (see PanelTab). Switching tabs saves
// the panel into the tab it leaves and shows the other tab's state.
public sealed partial class PaneViewModel
{
    /// <summary>True while a tab's state is put back: not "leaving" a server or a locked tab.</summary>
    private bool _showingTab;

    /// <summary>
    /// Called before the panel goes elsewhere by the user's doing (a folder, Back, a drive, a hot path, a command for
    /// the other panel): a locked tab is kept, and the panel goes on in a new tab next to it.
    /// </summary>
    public Action<PaneViewModel>? LeavingTab { get; set; }

    /// <summary>Saves the panel's state into its tab (all but the lock, which is the tab's own).</summary>
    public void SaveInto(PanelTab tab)
    {
        tab.Path = IsThisPc ? ThisPcProvider.RootPath : _path;
        tab.SortMode = _sortMode;
        tab.SortDescending = _sortDescending;
        tab.ViewMode = _defaultView;
        tab.FocusName = FocusedItem is { } focused && !focused.Entry.IsUpDir ? focused.Entry.Name : null;
        tab.Back = _history.BackEntries.ToList();
        tab.Forward = _history.ForwardEntries.ToList();
        tab.Marked = _items.Where(i => i.IsMarked).Select(i => i.Entry.Name).ToList();
        tab.Filter = _filter;
    }

    /// <summary>Shows a tab: its folder with its caret, marks, sort, view, filter and history.</summary>
    /// <summary>
    /// Waits until a frame has been drawn (what changed so far is on screen), at most 50 ms (a minimized
    /// window draws none). The work after it doesn't hold that frame back.
    /// </summary>
    internal Task NextFrameAsync()
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            drawn.TrySetResult();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;
        DispatcherQueueTimer timeout = _dispatcher.CreateTimer();
        timeout.Interval = TimeSpan.FromMilliseconds(50);
        timeout.IsRepeating = false;
        timeout.Tick += (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            drawn.TrySetResult();
        };
        timeout.Start();
        return drawn.Task.ContinueWith(_ => timeout.Stop(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public async Task ShowTabAsync(PanelTab tab)
    {
        _history.Load(tab.Back, tab.Forward);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        SortMode = tab.SortMode;
        SortDescending = tab.SortDescending;
        _defaultView = Enum.IsDefined(tab.ViewMode) ? tab.ViewMode : ViewMode.Detailed;
        if (!string.Equals(_filter, tab.Filter, StringComparison.Ordinal))
        {
            _filter = tab.Filter ?? string.Empty;
            _filterMask = _filter.Length == 0 ? null : new WildcardMask(_filter);
            OnPropertyChanged(nameof(Filter));
            OnPropertyChanged(nameof(FilterText));
        }
        _lastListedPath = null; // the tab's view, even in the same folder

        _showingTab = true;
        try
        {
            string path = string.IsNullOrWhiteSpace(tab.Path) ? ThisPcProvider.RootPath : tab.Path;
            IFileSystemProvider? provider = _registry.Find(path);
            if (provider is LocalFileSystemProvider)
                path = PathUtil.NormalizeDirectory(path);
            var marks = new HashSet<string>(tab.Marked, StringComparer.OrdinalIgnoreCase);
            bool shown = provider != null && await LoadAsync(provider, path, tab.FocusName, keepCaret: false, marks);
            if (!shown && !_loadCancelled)
            {
                // the tab's folder is gone (a removed drive, a deleted folder): the nearest place there is
                _path = path;
                await GoToAccessibleAsync();
            }
        }
        finally
        {
            _showingTab = false;
        }
    }
}
