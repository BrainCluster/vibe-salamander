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

/// <summary>
/// Salamander ordering: ".." first, then directories, then files. Directories are always sorted by
/// name (ascending/descending follows the panel); files by the chosen column with name as tie-breaker.
/// </summary>
public sealed class EntryComparer : IComparer<FileEntry>
{
    private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;

    public EntryComparer(SortMode mode, bool descending)
    {
        Mode = mode;
        Descending = descending;
    }

    public SortMode Mode { get; }
    public bool Descending { get; }

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
            result = Mode == SortMode.Date ? x.LastWrite.CompareTo(y.LastWrite) : NameComparer.Compare(x.Name, y.Name);
            if (result == 0) result = NameComparer.Compare(x.Name, y.Name);
        }
        else
        {
            result = Mode switch
            {
                SortMode.Extension => NameComparer.Compare(x.Extension, y.Extension),
                SortMode.Size => x.Size.CompareTo(y.Size),
                SortMode.Date => x.LastWrite.CompareTo(y.LastWrite),
                SortMode.Attributes => ((int)x.Attributes).CompareTo((int)y.Attributes),
                _ => 0,
            };
            if (result == 0)
                result = NameComparer.Compare(x.BaseName, y.BaseName);
            if (result == 0)
                result = NameComparer.Compare(x.Extension, y.Extension);
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
