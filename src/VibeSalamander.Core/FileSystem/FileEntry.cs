namespace VibeSalamander.Core.FileSystem;

/// <summary>
/// One item of a panel listing: a file, a directory, the ".." up-dir or a provider-specific item
/// (for example a drive in This PC). Immutable after the listing is created.
/// </summary>
public sealed class FileEntry
{
    public FileEntry(string name, string fullPath, bool isDirectory, long size, DateTime lastWrite,
                     FileAttributes attributes, bool isUpDir = false, object? tag = null)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
        LastWrite = lastWrite;
        Attributes = attributes;
        IsUpDir = isUpDir;
        Tag = tag;

        // Salamander splits files into Name + Ext columns; directories keep the whole name.
        int dot = isDirectory || isUpDir ? -1 : name.LastIndexOf('.');
        if (dot > 0) // ".gitignore" has no extension in the Ext column
        {
            BaseName = name[..dot];
            Extension = name[(dot + 1)..];
        }
        else
        {
            BaseName = name;
            Extension = string.Empty;
        }
    }

    /// <summary>File name with extension ("report.txt").</summary>
    public string Name { get; }

    /// <summary>Name without extension for files, full name for directories ("report").</summary>
    public string BaseName { get; }

    /// <summary>Extension without the dot ("txt"), empty for directories.</summary>
    public string Extension { get; }

    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsUpDir { get; }
    public long Size { get; }

    /// <summary>Last write time in local time.</summary>
    public DateTime LastWrite { get; }

    public FileAttributes Attributes { get; }

    /// <summary>Provider data (e.g. <see cref="DriveEntryInfo"/> for drives in This PC).</summary>
    public object? Tag { get; }

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    public static FileEntry CreateUpDir(string parentPath) =>
        new("..", parentPath, isDirectory: true, 0, DateTime.MinValue, FileAttributes.Directory, isUpDir: true);

    public override string ToString() => Name;
}
