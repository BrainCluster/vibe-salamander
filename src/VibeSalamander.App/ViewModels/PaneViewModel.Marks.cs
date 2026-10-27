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

// PaneViewModel: marking (the Salamander selection) and quick search.
public sealed partial class PaneViewModel
{
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
}
