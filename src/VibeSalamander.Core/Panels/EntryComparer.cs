// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Panels;

public enum SortMode
{
    Name,
    Extension,
    Size,
    Date,
    Attributes,
}

/// <summary>Sorting options of Configuration → Panels.</summary>
public sealed record SortOptions(
    bool Regional = true,
    bool Numerical = true,
    bool NewestFirst = true,
    bool DirectoriesByNameOnTimeSort = false,
    bool DirectoriesHaveExtensions = false)
{
    public static SortOptions Default { get; } = new();
}

/// <summary>
/// Salamander ordering: ".." first, then directories, then files. Directories are sorted by name
/// (or by time when sorting by date); files by the chosen column with name as tie-breaker.
/// </summary>
public sealed class EntryComparer : IComparer<FileEntry>
{
    private readonly SortOptions _options;
    private readonly StringComparer _strings;

    public EntryComparer(SortMode mode, bool descending, SortOptions? options = null)
    {
        Mode = mode;
        Descending = descending;
        _options = options ?? SortOptions.Default;
        _strings = _options.Regional ? StringComparer.CurrentCultureIgnoreCase : StringComparer.OrdinalIgnoreCase;
    }

    public SortMode Mode { get; }
    public bool Descending { get; }

    /// <summary>Name comparison: regional or ordinal, with numbers compared by value when Numerical is set.</summary>
    public int CompareNames(string a, string b) => _options.Numerical ? NaturalCompare(a, b, _strings) : _strings.Compare(a, b);

    /// <summary>"file2" before "file10": digit runs compare by their value.</summary>
    public static int NaturalCompare(string a, string b, StringComparer strings)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                ReadOnlySpan<char> na = a.AsSpan(si, i - si).TrimStart('0');
                ReadOnlySpan<char> nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length)
                    return na.Length.CompareTo(nb.Length);
                int c = na.SequenceCompareTo(nb);
                if (c != 0)
                    return c;
            }
            else
            {
                int si = i, sj = j;
                while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
                int c = strings.Compare(a.Substring(si, i - si), b.Substring(sj, j - sj));
                if (c != 0)
                    return c;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>The extension used for sorting ("txt"); directories have one only when the option says so.</summary>
    private string ExtensionOf(FileEntry e)
    {
        if (!e.IsDirectory)
            return e.Extension;
        if (!_options.DirectoriesHaveExtensions)
            return string.Empty;
        int dot = e.Name.LastIndexOf('.');
        return dot > 0 ? e.Name[(dot + 1)..] : string.Empty;
    }

    private int CompareTimes(FileEntry x, FileEntry y)
    {
        int c = x.LastWrite.CompareTo(y.LastWrite);
        return _options.NewestFirst ? -c : c;
    }

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (x.IsUpDir != y.IsUpDir) return x.IsUpDir ? -1 : 1;
        if (x.IsDirectory != y.IsDirectory) return x.IsDirectory ? -1 : 1;

        int result;
        if (x.IsDirectory)
        {
            result = Mode switch
            {
                SortMode.Date when !_options.DirectoriesByNameOnTimeSort => CompareTimes(x, y),
                SortMode.Extension when _options.DirectoriesHaveExtensions => _strings.Compare(ExtensionOf(x), ExtensionOf(y)),
                _ => 0,
            };
            if (result == 0) result = CompareNames(x.Name, y.Name);
        }
        else
        {
            result = Mode switch
            {
                SortMode.Extension => _strings.Compare(x.Extension, y.Extension),
                SortMode.Size => x.Size.CompareTo(y.Size),
                SortMode.Date => CompareTimes(x, y),
                SortMode.Attributes => ((int)x.Attributes).CompareTo((int)y.Attributes),
                _ => 0,
            };
            if (result == 0)
                result = CompareNames(x.BaseName, y.BaseName);
            if (result == 0)
                result = _strings.Compare(x.Extension, y.Extension);
        }
        return Descending ? -result : result;
    }

    /// <summary>Sorts a listing. ".." and the directories stay on top in both directions.</summary>
    public List<FileEntry> Sort(IEnumerable<FileEntry> entries)
    {
        var list = new List<FileEntry>(entries);
        list.Sort(this);
        return list;
    }
}
