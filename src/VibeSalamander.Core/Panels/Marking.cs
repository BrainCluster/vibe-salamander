// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Panels;

/// <summary>One row of a panel as the selection rules see it.</summary>
public interface IMarkable
{
    FileEntry Entry { get; }

    /// <summary>A drive in This PC (drives can't be marked).</summary>
    bool IsDrive { get; }

    bool IsMarked { get; set; }
}

/// <summary>
/// Salamander's selection ("marks") over the rows of a panel: which rows can be marked, and every way of
/// marking them. ".." and drives are never marked. The panel tells the user interface afterwards.
/// </summary>
public static class Marking
{
    public static bool CanMark(IMarkable item) => !item.Entry.IsUpDir && !item.IsDrive;

    public static List<T> Marked<T>(IReadOnlyList<T> items) where T : IMarkable
    {
        var marked = new List<T>();
        foreach (T item in items)
            if (item.IsMarked)
                marked.Add(item);
        return marked;
    }

    public static List<string> MarkedNames(IReadOnlyList<IMarkable> items)
    {
        var names = new List<string>();
        foreach (IMarkable item in items)
            if (item.IsMarked)
                names.Add(item.Entry.Name);
        return names;
    }

    /// <summary>What an operation works on: the marked rows, or the focused row when nothing is marked.</summary>
    public static IReadOnlyList<T> OperationItems<T>(IReadOnlyList<T> items, T? focused) where T : class, IMarkable
    {
        List<T> marked = Marked(items);
        if (marked.Count > 0)
            return marked;
        return focused != null && CanMark(focused) ? new[] { focused } : Array.Empty<T>();
    }

    /// <summary>Flips the row's mark; false when it can't be marked (or the index is outside the list).</summary>
    public static bool Toggle(IReadOnlyList<IMarkable> items, int index)
    {
        if (index < 0 || index >= items.Count || !CanMark(items[index]))
            return false;
        items[index].IsMarked = !items[index].IsMarked;
        return true;
    }

    /// <summary>Sets the mark of one row (nothing for ".." and drives).</summary>
    public static void Set(IReadOnlyList<IMarkable> items, int index, bool mark)
    {
        if (index >= 0 && index < items.Count && CanMark(items[index]))
            items[index].IsMarked = mark;
    }

    /// <summary>Marks or unmarks the rows from <paramref name="from"/> to <paramref name="to"/> (inclusive, either order, clamped to the list).</summary>
    public static void SetRange(IReadOnlyList<IMarkable> items, int from, int to, bool mark)
    {
        if (items.Count == 0)
            return;
        int lo = Math.Clamp(Math.Min(from, to), 0, items.Count - 1);
        int hi = Math.Clamp(Math.Max(from, to), 0, items.Count - 1);
        for (int i = lo; i <= hi; i++)
            if (CanMark(items[i]))
                items[i].IsMarked = mark;
    }

    public static void SetAll(IReadOnlyList<IMarkable> items, bool mark)
    {
        foreach (IMarkable item in items)
            if (CanMark(item))
                item.IsMarked = mark;
    }

    /// <summary>Inverts the marks of the files (and the directories too when <paramref name="includeDirectories"/>).</summary>
    public static void Invert(IReadOnlyList<IMarkable> items, bool includeDirectories)
    {
        foreach (IMarkable item in items)
            if (CanMark(item) && (includeDirectories || !item.Entry.IsDirectory))
                item.IsMarked = !item.IsMarked;
    }

    /// <summary>Marks (adds to the marks) the rows with these names (Restore Selection).</summary>
    public static void AddNames(IReadOnlyList<IMarkable> items, IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (IMarkable item in items)
            if (CanMark(item) && set.Contains(item.Entry.Name))
                item.IsMarked = true;
    }

    /// <summary>Marks exactly the rows with these names (Load Selection, Find's results); returns how many were found.</summary>
    public static int SetNames(IReadOnlyList<IMarkable> items, IReadOnlyCollection<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int found = 0;
        foreach (IMarkable item in items)
        {
            if (!CanMark(item))
                continue;
            bool mark = set.Contains(item.Entry.Name);
            item.IsMarked = mark;
            if (mark)
                found++;
        }
        return found;
    }

    /// <summary>(Un)marks the rows whose names match the mask (Num + / Num −); returns how many matched.</summary>
    public static int SetByMask(IReadOnlyList<IMarkable> items, WildcardMask mask, bool includeDirectories, bool mark)
    {
        int count = 0;
        foreach (IMarkable item in items)
        {
            if (!CanMark(item) || (item.Entry.IsDirectory && !includeDirectories))
                continue;
            if (mask.IsMatch(item.Entry.Name))
            {
                item.IsMarked = mark;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// (Un)marks the rows with the focused row's extension (Shift+Num ±) or name without the extension
    /// (Ctrl+Shift+Num ±): files with files, directories with directories.
    /// </summary>
    public static void SetSame(IReadOnlyList<IMarkable> items, IMarkable? focused, bool byExtension, bool mark)
    {
        if (focused == null || !CanMark(focused))
            return;
        string key = byExtension ? focused.Entry.Extension : focused.Entry.BaseName;
        foreach (IMarkable item in items)
        {
            if (!CanMark(item) || item.Entry.IsDirectory != focused.Entry.IsDirectory)
                continue;
            string value = byExtension ? item.Entry.Extension : item.Entry.BaseName;
            if (string.Equals(value, key, StringComparison.OrdinalIgnoreCase))
                item.IsMarked = mark;
        }
    }

    /// <summary>The previous (<paramref name="direction"/> −1) or next (+1) marked row from <paramref name="from"/>, or −1.</summary>
    public static int FindMarked(IReadOnlyList<IMarkable> items, int from, int direction)
    {
        for (int i = from + direction; i >= 0 && i < items.Count; i += direction)
            if (items[i].IsMarked)
                return i;
        return -1;
    }
}

/// <summary>Where quick search (typing in a panel) goes: the row and the search text after a key.</summary>
public readonly record struct QuickSearchResult(int Index, string Text);

/// <summary>Quick search: typing jumps to the first name that starts with the typed text.</summary>
public static class QuickSearchRules
{
    /// <summary>
    /// The text grows by <paramref name="c"/> when some name starts with it. The same letter again ("a", "a", ...)
    /// goes through the names starting with it after the caret, from the top again after the last one, like
    /// Explorer, unless a name starts with the repeated letters ("aa"). Null: no match (the key is ignored).
    /// </summary>
    public static QuickSearchResult? Type(IReadOnlyList<IMarkable> items, string text, char c, int caret)
    {
        string longer = text + c;
        int index = FindByPrefix(items, longer, 0);
        if (index >= 0)
            return new QuickSearchResult(index, longer);
        if (IsRepeatedLetter(longer))
        {
            string first = c.ToString();
            int next = FindByPrefix(items, first, caret + 1);
            if (next >= 0)
                return new QuickSearchResult(next, first);
        }
        return null;
    }

    /// <summary>The search text without its last character, and where that leaves the caret (it stays when the text is empty).</summary>
    public static QuickSearchResult? Backspace(IReadOnlyList<IMarkable> items, string text, int caret)
    {
        if (text.Length == 0)
            return null;
        string shorter = text[..^1];
        return new QuickSearchResult(shorter.Length == 0 ? caret : FindByPrefix(items, shorter, 0), shorter);
    }

    /// <summary>The first row from <paramref name="start"/> on (wrapping around) whose name starts with the prefix, or −1.</summary>
    public static int FindByPrefix(IReadOnlyList<IMarkable> items, string prefix, int start)
    {
        for (int i = 0; i < items.Count; i++)
        {
            int index = (Math.Max(0, start) + i) % items.Count;
            if (items[index].Entry.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                return index;
        }
        return -1;
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
}
