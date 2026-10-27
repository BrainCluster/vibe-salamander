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

// PaneViewModel: marking (the Salamander selection) and quick search, applying the rules of Core.Panels.
public sealed partial class PaneViewModel
{
    // ---- marks (Salamander selection): the rules are in Core.Panels.Marking -------------------

    public IReadOnlyList<FileItemViewModel> MarkedItems => Marking.Marked(_items);

    /// <summary>Items an operation applies to: the marked items, or the focused item when nothing is marked.</summary>
    public IReadOnlyList<FileItemViewModel> GetOperationItems() => Marking.OperationItems(_items, FocusedItem);

    public void ToggleMark(int index)
    {
        if (Marking.Toggle(_items, index))
            NotifyMarksChanged();
    }

    /// <summary>Marks or unmarks the items from <paramref name="from"/> to <paramref name="to"/> (inclusive, any order).</summary>
    public void MarkRange(int from, int to, bool mark)
    {
        if (_items.Count == 0)
            return;
        Marking.SetRange(_items, from, to, mark);
        NotifyMarksChanged();
    }

    /// <summary>Sets the mark of one item (".." and drives can't be marked); call NotifyMarksChanged after a batch.</summary>
    public void SetMark(int index, bool mark) => Marking.Set(_items, index, mark);

    /// <summary>Updates the information line after marks changed.</summary>
    public void NotifyMarksChanged() => OnPropertyChanged(nameof(InfoText));

    /// <summary>Remembers the marked names for Restore Selection (Ctrl+W).</summary>
    public void RememberSelection()
    {
        List<string> names = Marking.MarkedNames(_items);
        if (names.Count > 0)
            _rememberedSelection = names;
    }

    public void RestoreSelection()
    {
        Marking.AddNames(_items, _rememberedSelection);
        NotifyMarksChanged();
    }

    /// <summary>Select/Unselect by mask (Num + / Num -).</summary>
    public int MarkByMask(string mask, bool includeDirectories, bool mark)
    {
        int count = Marking.SetByMask(_items, new WildcardMask(mask), includeDirectories, mark);
        NotifyMarksChanged();
        return count;
    }

    /// <summary>(Un)marks all files with the focused file's extension (Shift+Num ±) or base name (Ctrl+Shift+Num ±).</summary>
    public void MarkSame(bool byExtension, bool mark)
    {
        if (FocusedItem is not { } focused || !Marking.CanMark(focused))
            return;
        Marking.SetSame(_items, focused, byExtension, mark);
        NotifyMarksChanged();
    }

    /// <summary>Index of the previous/next marked item from the caret (Alt+Up / Alt+Down), or -1.</summary>
    public int FindMarked(int direction) => Marking.FindMarked(_items, _caretIndex, direction);

    public void MarkAll(bool mark)
    {
        if (!mark)
            RememberSelection();
        Marking.SetAll(_items, mark);
        NotifyMarksChanged();
    }

    /// <summary>Inverts the marks of the files (and directories when Configuration → Panels includes them).</summary>
    public void InvertMarks()
    {
        Marking.Invert(_items, UiState.SelectIncludesDirectories);
        NotifyMarksChanged();
    }

    // ---- quick search: the rules are in Core.Panels.QuickSearchRules -------------------------

    /// <summary>Adds a typed character to the quick search and moves the caret; returns the new caret or -1.</summary>
    public int QuickSearchType(char c)
    {
        if (QuickSearchRules.Type(_items, _quickSearch, c, _caretIndex) is not { } result)
            return -1; // no match: ignore the character (Salamander beeps)
        QuickSearch = result.Text;
        return result.Index;
    }

    public int QuickSearchBackspace()
    {
        if (QuickSearchRules.Backspace(_items, _quickSearch, _caretIndex) is not { } result)
            return -1;
        QuickSearch = result.Text;
        return result.Index;
    }

    public void EndQuickSearch() => QuickSearch = string.Empty;
}
