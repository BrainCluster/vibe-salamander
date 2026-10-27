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

// PaneViewModel: the rows: hidden names, sorting and rebuilding the list.
public sealed partial class PaneViewModel
{
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
        int found = Core.Panels.Marking.SetNames(_items, names);
        NotifyMarksChanged();
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

        if (IsThisPc)
            items = items.OrderBy(DriveGroupOf).ToList(); // in the order of their groups (a stable sort)

        int caret = -1;
        if (!string.IsNullOrEmpty(focusName))
            caret = items.FindIndex(i => string.Equals(i.Entry.Name, focusName, StringComparison.OrdinalIgnoreCase));
        if (caret < 0)
            caret = items.Count == 0 ? -1 : Math.Clamp(fallbackCaret, 0, items.Count - 1);

        bool samePlace = string.Equals(_lastListedPath, _path, StringComparison.OrdinalIgnoreCase);
        _lastListedPath = _path;
        Items = items;
        _groupedItems = IsThisPc ? GroupDrives(items) : null;
        OnPropertyChanged(nameof(ItemsSource));
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
        settings.ViewMode = _defaultView;
    }
}
